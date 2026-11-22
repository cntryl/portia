using Cntryl.Fitz;

namespace Portia.Accounts.Tests;

/// <summary>Exercises the account reducer and repository on isolated Fitz resources.</summary>
public sealed class AccountRebuildTests
{
    /// <summary>Candidate replay starts empty and resumes independently from its durable checkpoint.</summary>
    [Fact]
    [Trait("Category", "BrokerIntegration")]
    public async Task ShouldResumeCandidateGivenCommittedLiveAccount()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var endpoint = Environment.GetEnvironmentVariable("FITZ_TEST_ENDPOINT") ?? "ws://127.0.0.1:4090/ws";
        await using var client = new FitzClient(new ClientConfig(new Uri(endpoint)));
        await client.ConnectWhenReadyAsync(new ConnectWhenReadyOptions(TimeSpan.FromSeconds(15)), cancellationToken);
        var route = "kv://portia/accounts/test-" + Uuid.CreateVersion4();
        var account = Uuid.CreateVersion4();
        var pattern = EventStreamPattern.ForPattern("test", "accounts");
        var live = new CheckpointIdentity(AccountRepository.ProjectorName, pattern);
        var candidate = new CheckpointIdentity(live.ComponentName, pattern, "first");
        var second = new CheckpointIdentity(live.ComponentName, pattern, "second");
        var events = new InMemoryEventStore();
        await events.AppendAsync(new EventStreamAddress("test", "accounts", account.ToString()), 0,
            [DomainEventSeed.Attach(new AccountOpened(500), account, 1),
             DomainEventSeed.Attach(new FundsDeposited(125), account, 2)], cancellationToken);
        var services = new ServiceCollection();
        services.AddScoped<IAccountProjectionRepository>(_ => new AccountRepository(client.Kv, route));
        services.AddScoped<IProjectionStore>(_ => throw new InvalidOperationException("Unexpected store resolution."));
        services.AddPortia().AddProjector<AccountProjector>(live.ComponentName, pattern);
        await using var provider = services.BuildServiceProvider();
        async Task RunAsync(CheckpointIdentity identity, int limit)
        {
            await using var scope = provider.CreateAsyncScope();
            var projector = scope.ServiceProvider.GetRequiredService<ProjectorRegistration>().Resolve(scope.ServiceProvider);
            var store = scope.ServiceProvider.GetRequiredService<IAccountProjectionRepository>();
            await new ProjectorRunner(events).RunAsync(projector, await store.LoadCheckpointAsync(identity, cancellationToken),
                new ProjectionRunOptions { RebuildId = identity.RebuildId, MaxEventsPerPass = limit }, cancellationToken);
        }

        await RunAsync(live, 2);

        // Act
        await RunAsync(candidate, 1);
        var liveMidpoint = await new AccountRepository(client.Kv, route).GetAsync(new TenantId("test"), account, cancellationToken);
        var candidateMidpoint = await new AccountRepository(client.Kv, route).GetAsync(candidate, account, cancellationToken);
        await RunAsync(candidate, 2);
        await RunAsync(candidate, 2);

        // Assert
        Assert.Equal(new AccountReadModel(account, 625), liveMidpoint);
        Assert.Equal(new AccountReadModel(account, 500), candidateMidpoint);
        var query = new AccountRepository(client.Kv, route);
        Assert.Equal(liveMidpoint, await query.GetAsync(candidate, account, cancellationToken));
        Assert.Null(await query.GetAsync(second, account, cancellationToken));
        Assert.Equal(ProjectionCheckpoint.Start, await query.LoadCheckpointAsync(second, cancellationToken));
    }

    /// <summary>Only earlier committed batches survive a deposit handler failure.</summary>
    [Theory]
    [InlineData(512, false)]
    [InlineData(1, false)]
    [InlineData(512, true)]
    public async Task ShouldRetryFailedDepositGivenConfiguredBatchSize(int batchSize, bool cancel)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var endpoint = Environment.GetEnvironmentVariable("FITZ_TEST_ENDPOINT") ?? "ws://127.0.0.1:4090/ws";
        await using var client = new FitzClient(new ClientConfig(new Uri(endpoint)));
        await client.ConnectWhenReadyAsync(new ConnectWhenReadyOptions(TimeSpan.FromSeconds(15)), cancellationToken);
        var route = "kv://portia/accounts/test-" + Uuid.CreateVersion4();
        var account = Uuid.CreateVersion4();
        var pattern = EventStreamPattern.ForPattern("test", "accounts");
        var identity = new CheckpointIdentity(AccountRepository.ProjectorName, pattern);
        var events = new InMemoryEventStore();
        await events.AppendAsync(new EventStreamAddress("test", "accounts", account.ToString()), 0,
            [DomainEventSeed.Attach(new AccountOpened(500), account, 1),
             DomainEventSeed.Attach(new FundsDeposited(125), account, 2)], cancellationToken);
        using var cancellation = new CancellationTokenSource();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancellation.Token);
        var failDeposit = true;
        void OnDeposit()
        {
            if (!failDeposit)
                return;
            if (cancel)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }

            throw new InvalidOperationException("Injected failure after staging the deposit.");
        }
        var services = new ServiceCollection();
        services.AddScoped<IAccountProjectionRepository>(_ =>
            new DepositFailureRepository(new AccountRepository(client.Kv, route), OnDeposit));
        services.AddScoped<IProjectionStore>(_ => throw new InvalidOperationException("Unexpected store resolution."));
        services.AddPortia().AddProjector<AccountProjector>(identity.ComponentName, pattern);
        await using var provider = services.BuildServiceProvider();
        async Task RunAsync()
        {
            await using var scope = provider.CreateAsyncScope();
            var projector = scope.ServiceProvider.GetRequiredService<ProjectorRegistration>().Resolve(scope.ServiceProvider);
            var checkpoint = await new AccountRepository(client.Kv, route).LoadCheckpointAsync(identity, cancellationToken);
            await new ProjectorRunner(events).RunAsync(projector, checkpoint,
                new ProjectionRunOptions { MaxBatchSize = batchSize }, failDeposit && cancel ? linkedCancellation.Token : cancellationToken);
        }

        // Act
        var exception = await Record.ExceptionAsync(RunAsync);
        var fresh = new AccountRepository(client.Kv, route);
        var afterFailure = await fresh.GetAsync(identity, account, cancellationToken);
        var progress = await fresh.LoadCheckpointAsync(identity, cancellationToken);
        failDeposit = false;
        await RunAsync();
        await RunAsync();

        // Assert
        if (cancel)
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
        else
            Assert.Equal(ProcessorFailureStage.Handling, Assert.IsType<ProcessorFailureException>(exception).Stage);
        Assert.Equal(batchSize == 1 ? new AccountReadModel(account, 500) : null, afterFailure);
        Assert.Equal(batchSize != 1, progress == ProjectionCheckpoint.Start);
        Assert.Equal(new AccountReadModel(account, 625), await fresh.GetAsync(identity, account, cancellationToken));
    }

    sealed class DepositFailureRepository(AccountRepository repository, Action onDeposit) : IAccountProjectionRepository
    {
        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct) =>
            repository.LoadCheckpointAsync(identity, ct);

        public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct) =>
            repository.BeginAsync(context, ct);

        public ValueTask<AccountReadModel?> GetForUpdateAsync(Uuid accountId, CancellationToken ct) =>
            repository.GetForUpdateAsync(accountId, ct);

        public async ValueTask SaveAsync(AccountReadModel account, CancellationToken ct)
        {
            await repository.SaveAsync(account, ct);
            if (account.BalanceCents == 625)
                onDeposit();
        }
    }
}
