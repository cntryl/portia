using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Portia.Accounts.Tests;

/// <summary>Exercises the Account feature through its shared composition and Portia's real request lifecycle.</summary>
public sealed class AccountFeatureTests
{
    static readonly TenantId Tenant = new("tenant-a");
    static readonly Uuid AccountId = Uuid.CreateVersion4();

    /// <summary>The copied sample must not silently configure its demo tenant in production.</summary>
    [Fact]
    public void ShouldRejectRegistrationGivenNoConfiguredTenantWhenEnvironmentIsProduction()
    {
        // Arrange
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        var environment = new TestHostEnvironment(Environments.Production);

        // Act
        var exception = Record.Exception(() =>
            services.AddAccountsApplication(configuration, environment));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>The local demo tenant remains convenient in Development.</summary>
    [Fact]
    public void ShouldServeDemoTenantGivenNoConfiguredTenantWhenEnvironmentIsDevelopment()
    {
        // Arrange
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fitz:Endpoint"] = "ws://127.0.0.1:4090/ws"
            })
            .Build();
        var environment = new TestHostEnvironment(Environments.Development);

        // Act
        _ = services.AddAccountsApplication(configuration, environment);
        using var provider = services.BuildServiceProvider();
        var serves = provider.GetRequiredService<IAccountTenantScope>().Serves(new TenantId("demo"));

        // Assert
        Assert.True(serves);
    }

    /// <summary>Authorization, behavior, guard, and handler run in the declared order.</summary>
    [Fact]
    public async Task ShouldRunPipelineStagesInOrderGivenOpenAccountWhenDepositing()
    {
        // Arrange
        var trace = new RecordingAccountLifecycle();
        await using var services = CreateServices(trace, available: true);
        var actor = AccountActor(AccountId);

        await RequestScenario.For(services).GivenActor(actor)
            .When(new OpenAccount(Tenant, AccountId, 500))
            .ExpectAuthorized()
            .ExpectGuardPassed<AccountTenantGuard>()
            .ExpectGuardPassed<AccountOperationsGuard>()
            .ExpectHandled()
            .ExpectSuccess();

        trace.Clear();

        // Act
        var exception = await Record.ExceptionAsync(async () => await RequestScenario.For(services).GivenActor(actor)
            .When(new DepositFunds(Tenant, AccountId, 125))
            .ExpectAuthorized()
            .ExpectGuardPassed<AccountTenantGuard>()
            .ExpectGuardPassed<AccountOperationsGuard>()
            .ExpectHandled()
            .ExpectSuccess());

        // Assert
        Assert.Null(exception);
        Assert.Equal(["authorizer", "behavior.enter", "tenant.guard", "guard", "handler.deposit", "behavior.exit"],
            trace.Stages);
    }

    /// <summary>A failed guard stops before the repository handler while the behavior observes the result.</summary>
    [Fact]
    public async Task ShouldShortCircuitHandlerGivenPausedOperationsWhenDepositing()
    {
        // Arrange
        var trace = new RecordingAccountLifecycle();
        await using var services = CreateServices(trace, available: false);

        // Act
        var exception = await Record.ExceptionAsync(async () => await RequestScenario.For(services)
            .GivenActor(AccountActor(AccountId))
            .When(new DepositFunds(Tenant, AccountId, 125))
            .ExpectAuthorized()
            .ExpectGuardPassed<AccountTenantGuard>()
            .ExpectGuardFailed<AccountOperationsGuard>(RequestErrorKind.Conflict)
            .ExpectNotHandled()
            .ExpectFailure(RequestErrorKind.Conflict));

        // Assert
        Assert.Null(exception);
        Assert.Equal(["authorizer", "behavior.enter", "tenant.guard", "guard", "behavior.exit"], trace.Stages);
    }

    /// <summary>A failed authorizer stops before the behavior, guard, and handler.</summary>
    [Fact]
    public async Task ShouldShortCircuitBeforeBehaviorGivenActorWhoDoesNotOwnAccount()
    {
        // Arrange
        var trace = new RecordingAccountLifecycle();
        await using var services = CreateServices(trace, available: true);

        // Act
        var exception = await Record.ExceptionAsync(async () => await RequestScenario.For(services)
            .GivenActor(AccountActor(Uuid.CreateVersion4()))
            .When(new DepositFunds(Tenant, AccountId, 125))
            .ExpectDenied(RequestErrorKind.Forbidden)
            .ExpectNotHandled());

        // Assert
        Assert.Null(exception);
        Assert.Equal(["authorizer"], trace.Stages);
    }

    /// <summary>
    ///     The same account identifier in another tenant is denied by Portia's tenant authorizer before the
    ///     feature's own account authorizer runs.
    /// </summary>
    [Fact]
    public async Task ShouldDenyRequestGivenActorTenantDifferentFromRequestTenant()
    {
        // Arrange
        var trace = new RecordingAccountLifecycle();
        await using var services = CreateServices(trace, available: true);

        // Act
        var exception = await Record.ExceptionAsync(async () => await RequestScenario.For(services)
            .GivenActor(AccountActor(Tenant.Value, AccountId))
            .When(new DepositFunds(new TenantId("tenant-b"), AccountId, 125))
            .ExpectDenied(RequestErrorKind.Forbidden)
            .ExpectNotHandled());

        // Assert
        Assert.Null(exception);
        Assert.Empty(trace.Stages);
    }

    /// <summary>A tenant without a configured projector is rejected before any account handler runs.</summary>
    [Fact]
    public async Task ShouldRejectRequestGivenTenantOutsideConfiguredProjectorScope()
    {
        // Arrange
        var trace = new RecordingAccountLifecycle();
        await using var services = CreateServices(trace, available: true);

        // Act
        var exception = await Record.ExceptionAsync(async () => await RequestScenario.For(services)
            .GivenActor(RequestActor.System)
            .When(new OpenAccount(new TenantId("tenant-b"), AccountId, 500))
            .ExpectGuardFailed<AccountTenantGuard>(RequestErrorKind.Validation)
            .ExpectNotHandled()
            .ExpectFailure(RequestErrorKind.Validation));

        // Assert
        Assert.Null(exception);
        Assert.Equal(["authorizer", "tenant.guard"], trace.Stages);
    }

    /// <summary>The read query returns the repository value through the real request bus.</summary>
    [Fact]
    public async Task ShouldReturnReadModelGivenProjectedAccountWhenQueryingThroughHandler()
    {
        // Arrange
        var trace = new RecordingAccountLifecycle();
        var readModel = new AccountReadModel(AccountId, 625);
        await using var services = CreateServices(trace, available: true, readModel);

        // Act
        var result = await RequestScenario.For(services).GivenActor(AccountActor(AccountId))
            .When(new GetAccount(Tenant, AccountId))
            .ExpectAuthorized()
            .ExpectGuardPassed<AccountTenantGuard>()
            .ExpectGuardPassed<AccountOperationsGuard>()
            .ExpectHandled()
            .ExpectSuccess();

        // Assert
        Assert.Equal(readModel, result.Value);
    }

    /// <summary>The aggregate enforces positive deposits independently of guards and transports.</summary>
    [Fact]
    public void ShouldKeepBalanceUnchangedGivenZeroDepositWhenDepositing()
    {
        // Arrange
        var scenario = new AggregateScenario<Account>(new Account(Tenant, AccountId))
            .Given(new AccountOpened(500));

        // Act
        var result = scenario.WhenCommitOnSuccess(account => account.Deposit(0));

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Validation, result.Error!.Kind);
        Assert.Equal(500, scenario.Aggregate.BalanceCents);
        Assert.Empty(scenario.PendingEvents);
    }

    /// <summary>The projector folds account events through its pure reducer into the read repository.</summary>
    [Fact]
    public async Task ShouldProjectReadModelGivenOpeningAndDepositEvents()
    {
        // Arrange
        var scenario = new ProjectorScenario(Tenant).Given(
            DomainEventSeed.Attach(new AccountOpened(500), AccountId, 1),
            DomainEventSeed.Attach(new FundsDeposited(125), AccountId, 2));
        var repository = new InMemoryAccountRepository(scenario.Store);

        // Act
        await scenario.RunAsync(new AccountProjector(repository));
        var projected = await repository.GetAsync(Tenant, AccountId, default);

        // Assert
        Assert.Equal(new AccountReadModel(AccountId, 625), projected);
    }

    static ServiceProvider CreateServices(RecordingAccountLifecycle trace, bool available,
        AccountReadModel? readModel = null)
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton<IEventStore>(new InMemoryEventStore());
        _ = services.AddSingleton<IActorTenantResolver>(new ClaimTenantResolver("tenant_id"));
        _ = services.AddSingleton<IAccountAccess, ClaimAccountAccess>();
        _ = services.AddSingleton<IAccountTenantScope>(new FixedAccountTenantScope(Tenant));
        _ = services.AddSingleton<IAccountOperations>(new FixedAccountOperations(available));
        _ = services.AddSingleton<IAccountLifecycleObserver>(trace);
        _ = services.AddSingleton<IAccountReadRepository>(new InMemoryAccountRepository(initial: readModel));
        _ = services.AddScoped<IAccountCommandRepository, AccountCommandRepository>();
        _ = services.AddPortia().RequireAuthorization().AddAccountsFeature();
        return services.BuildServiceProvider(validateScopes: true);
    }

    static ClaimsPrincipal AccountActor(Uuid accountId) => AccountActor(Tenant.Value, accountId);

    static ClaimsPrincipal AccountActor(string tenant, Uuid accountId) => new(new ClaimsIdentity(
        [new Claim("account_id", accountId.ToString()),
            new Claim("tenant_id", tenant),
            new Claim("sub", "account-test-user", ClaimValueTypes.String, "https://accounts.example.test")],
        "AccountTests"));

    sealed class FixedAccountTenantScope(TenantId tenant) : IAccountTenantScope
    {
        public bool Serves(TenantId candidate) => candidate == tenant;
    }

    sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Portia.Accounts.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    sealed class FixedAccountOperations(bool available) : IAccountOperations
    {
        public bool IsAvailable(TenantId tenant) => available;
    }

    sealed class RecordingAccountLifecycle : IAccountLifecycleObserver
    {
        readonly ConcurrentQueue<string> _stages = new();

        public string[] Stages => _stages.ToArray();

        public void Clear()
        {
            while (_stages.TryDequeue(out _))
            {
            }
        }

        public void Record(string stage) => _stages.Enqueue(stage);
    }

    sealed class InMemoryAccountRepository(IProjectionStore? store = null, AccountReadModel? initial = null)
        : IAccountReadRepository, IAccountProjectionRepository
    {
        readonly Dictionary<Uuid, AccountReadModel> _accounts = initial is null
            ? []
            : new Dictionary<Uuid, AccountReadModel> { [initial.AccountId] = initial };

        readonly IProjectionStore _store = store ?? new ProjectorScenario().Store;

        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct = default) =>
            _store.LoadCheckpointAsync(identity, ct);

        public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default) =>
            _store.BeginAsync(context, ct);

        public ValueTask<AccountReadModel?> GetAsync(TenantId tenant, Uuid accountId, CancellationToken ct) =>
            ValueTask.FromResult(_accounts.GetValueOrDefault(accountId));

        public ValueTask<AccountReadModel?> GetForUpdateAsync(Uuid accountId, CancellationToken ct) =>
            ValueTask.FromResult(_accounts.GetValueOrDefault(accountId));

        public ValueTask SaveAsync(AccountReadModel account, CancellationToken ct)
        {
            _accounts[account.AccountId] = account;
            return ValueTask.CompletedTask;
        }
    }
}
