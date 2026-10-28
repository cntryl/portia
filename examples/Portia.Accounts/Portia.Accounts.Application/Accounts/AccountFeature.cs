namespace Portia.Accounts;

/// <summary>One explicit feature composition shared by API, worker, and application tests.</summary>
public static class AccountFeature
{
    /// <summary>Selects the account contracts, lifecycle policies, handlers, and projector.</summary>
    /// <param name="builder">The shared Portia application composition.</param>
    /// <returns>The same builder for further host composition.</returns>
    public static PortiaBuilder AddAccountsFeature(this PortiaBuilder builder) => builder
        .AddRequestHandler<OpenAccountHandler>()
        .AddRequestHandler<DepositFundsHandler>()
        .AddRequestHandler<GetAccountHandler>()
        .AddRequestAuthorizer<TenantRequestAuthorizer>()
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
        var configured = configuration["Accounts:Tenant"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException(
                    "Configuration value 'Accounts:Tenant' is required outside Development.");
            configured = "demo";
        }

        var tenant = new TenantId(configured);
        services.TryAddSingleton<IAccountTenantScope>(_ => new ConfiguredAccountTenantScope(tenant));
        services.TryAddScoped<IAccountRepository, AccountRepository>();
        services.TryAddScoped<AccountProjectionRepository>(provider => new AccountProjectionRepository(
            provider.GetRequiredService<IKvClient>(), projectionRoute));
        services.TryAddScoped<IProjectionStore>(provider => provider.GetRequiredService<AccountProjectionRepository>());
        services.TryAddScoped<IAccountProjectionRepository>(provider =>
            provider.GetRequiredService<AccountProjectionRepository>());
        services.TryAddScoped<IAccountReadRepository>(provider => provider.GetRequiredService<AccountProjectionRepository>());
        services.TryAddSingleton<IActorTenantResolver>(_ => new ClaimTenantResolver("tenant_id"));
        services.TryAddSingleton<IAccountAccess, ClaimAccountAccess>();
        services.TryAddSingleton<IAccountOperations, ConfiguredAccountOperations>();
        services.TryAddSingleton<IAccountLifecycleObserver, LoggingAccountLifecycleObserver>();
        services.TryAddSingleton<ITenantDirectory>(_ => new FixedTenantDirectory(tenant));

        return services.AddPortia()
            .RequireAuthorization()
            .AddAccountsFeature()
            .AddFitz(configuration.GetSection("Fitz"), fitz => fitz.DisableRequestWorkers());
    }
}

/// <summary>Account-level authorization supplied by the host's authenticated principal.</summary>
public interface IAccountAccess
{
    /// <summary>Determines whether the principal may access the account.</summary>
    bool CanAccess(ClaimsPrincipal actor, Uuid accountId);
}

/// <summary>Identifies tenants served by the account feature's configured projector.</summary>
public interface IAccountTenantScope
{
    /// <summary>Reports whether the account feature serves the tenant.</summary>
    bool Serves(TenantId tenant);
}

/// <summary>Account operational switch used only as a preflight guard.</summary>
public interface IAccountOperations
{
    /// <summary>Reports whether account operations are currently available for the tenant.</summary>
    bool IsAvailable(TenantId tenant);
}

/// <summary>Receives bounded account lifecycle observations.</summary>
public interface IAccountLifecycleObserver
{
    /// <summary>Records one lifecycle stage without account identifiers or payloads.</summary>
    void Record(string stage);
}

/// <summary>
///     Authorizes account access using the account grant on the authenticated principal. Tenant membership is
///     Portia's <see cref="TenantRequestAuthorizer" />'s decision, made before this runs.
/// </summary>
public sealed class ClaimAccountAccess : IAccountAccess
{
    /// <inheritdoc />
    public bool CanAccess(ClaimsPrincipal actor, Uuid accountId) =>
        RequestActor.IsSystem(actor) || actor.HasClaim("account_id", accountId.ToString());
}

sealed class ConfiguredAccountTenantScope(TenantId configuredTenant) : IAccountTenantScope
{
    public bool Serves(TenantId tenant) => tenant == configuredTenant;
}

sealed class ConfiguredAccountOperations(IConfiguration configuration) : IAccountOperations
{
    public bool IsAvailable(TenantId tenant) =>
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
