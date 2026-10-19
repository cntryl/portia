namespace Portia.Accounts;

/// <summary>One explicit feature composition shared by API, worker, and application tests.</summary>
public static class AccountFeature
{
    /// <summary>Selects the account contracts, lifecycle policies, handlers, and projector.</summary>
    /// <param name="builder">The shared Portia application composition.</param>
    /// <returns>The same builder for further host composition.</returns>
    public static PortiaBuilder AddAccountsFeature(this PortiaBuilder builder) => builder
        .AddEvent<AccountOpened>()
        .AddEvent<FundsDeposited>()
        .AddRequestHandler<OpenAccountHandler>()
        .AddRequestHandler<DepositFundsHandler>()
        .AddRequestHandler<GetAccountHandler>()
        .AddRequestAuthorizer<AccountRequestAuthorizer>()
        .AddRequestPipelineBehavior<AccountRequestBehavior>(order: 100)
        .AddRequestGuard<AccountTenantGuard>()
        .AddRequestGuard<AccountOperationsGuard>()
        .AddProjector<AccountProjector>(AccountProjectionRepository.ProjectorName, WorkloadScope.PerTenant);
}

/// <summary>Central application setup reused by the API and worker hosts.</summary>
public static class AccountsApplication
{
    /// <summary>Registers shared services, account feature composition, and the Fitz client.</summary>
    /// <param name="services">Host services.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="environment">Host environment.</param>
    /// <returns>The configured Portia builder.</returns>
    public static PortiaBuilder AddAccountsApplication(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var projectionRoute = configuration["Accounts:ProjectionRoute"] ?? "kv://portia/accounts/read-model";
        var tenant = configuration["Accounts:Tenant"];
        if (string.IsNullOrWhiteSpace(tenant))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException(
                    "Configuration value 'Accounts:Tenant' is required outside Development.");
            tenant = "demo";
        }

        services.TryAddSingleton<IAccountTenantScope>(_ => new ConfiguredAccountTenantScope(tenant));
        services.TryAddScoped<IAccountRepository, AccountRepository>();
        services.TryAddScoped<AccountProjectionRepository>(provider => new AccountProjectionRepository(
            provider.GetRequiredService<IKvClient>(), projectionRoute));
        services.TryAddScoped<IProjectionStore>(provider => provider.GetRequiredService<AccountProjectionRepository>());
        services.TryAddScoped<IAccountProjectionRepository>(provider =>
            provider.GetRequiredService<AccountProjectionRepository>());
        services.TryAddScoped<IAccountReadRepository>(provider => provider.GetRequiredService<AccountProjectionRepository>());
        services.TryAddSingleton<IAccountAccess, ClaimAccountAccess>();
        services.TryAddSingleton<IAccountOperations, ConfiguredAccountOperations>();
        services.TryAddSingleton<IAccountLifecycleObserver, LoggingAccountLifecycleObserver>();
        services.TryAddSingleton<ITenantDirectory>(_ => new ConfiguredAccountTenantDirectory(tenant));

        return services.AddPortia()
            .RequireAuthorization()
            .AddAccountsFeature()
            .AddFitz(configuration.GetSection("Fitz"), fitz => fitz.DisableRequestWorkers());
    }
}

/// <summary>Account authorization supplied by the host's authenticated principal.</summary>
public interface IAccountAccess
{
    /// <summary>Determines whether the principal may access the account within the specified tenant.</summary>
    bool CanAccess(ClaimsPrincipal actor, string tenant, Uuid accountId);
}

/// <summary>Identifies tenants served by the account feature's configured projector.</summary>
public interface IAccountTenantScope
{
    /// <summary>Reports whether the account feature serves the tenant.</summary>
    bool Serves(string tenant);
}

/// <summary>Account operational switch used only as a preflight guard.</summary>
public interface IAccountOperations
{
    /// <summary>Reports whether account operations are currently available for the tenant.</summary>
    bool IsAvailable(string tenant);
}

/// <summary>Receives bounded account lifecycle observations.</summary>
public interface IAccountLifecycleObserver
{
    /// <summary>Records one lifecycle stage without account identifiers or payloads.</summary>
    void Record(string stage);
}

/// <summary>Authorizes account access using tenant and account grants on the authenticated principal.</summary>
public sealed class ClaimAccountAccess : IAccountAccess
{
    /// <inheritdoc />
    public bool CanAccess(ClaimsPrincipal actor, string tenant, Uuid accountId) =>
        RequestActor.IsSystem(actor)
        || (!string.IsNullOrWhiteSpace(tenant)
            && actor.HasClaim("tenant_id", tenant)
            && actor.HasClaim("account_id", accountId.ToString()));
}

sealed class ConfiguredAccountTenantScope(string configuredTenant) : IAccountTenantScope
{
    public bool Serves(string tenant) => string.Equals(tenant, configuredTenant, StringComparison.Ordinal);
}

sealed class ConfiguredAccountOperations(IConfiguration configuration) : IAccountOperations
{
    public bool IsAvailable(string tenant) =>
        !bool.TryParse(configuration["Accounts:Paused"], out var paused) || !paused;
}

sealed class LoggingAccountLifecycleObserver(ILogger<LoggingAccountLifecycleObserver> logger)
    : IAccountLifecycleObserver
{
    public void Record(string stage) => AccountLifecycleLog.Record(logger, stage);
}

static partial class AccountLifecycleLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Account request lifecycle stage {Stage}")]
    public static partial void Record(ILogger logger, string stage);
}

sealed class ConfiguredAccountTenantDirectory(string tenant) : ITenantDirectory
{
    public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new TenantId(tenant);
    }

    public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new TenantLifecycleChange(TenantLifecycleChangeKind.Added, new TenantId(tenant));
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
    }
}
