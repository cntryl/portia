using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
/// Runs a <see cref="MultiTenantRunner" /> for the life of the host, resolving the
/// <see cref="MultiTenantRunner" /> from <paramref name="services" /> lazily (at
/// <see cref="ExecuteAsync" /> time, not construction time) so its own dependencies don't have to
/// be resolvable before hosted services start. Registered by
/// <see cref="PortiaHostingServiceCollectionExtensions.AddPortiaMultiTenantRunner" /> — not meant
/// to be constructed directly.
/// </summary>
/// <param name="services">The container the runner and per-tenant callbacks resolve through.</param>
/// <param name="onTenantStarted">
/// Runs for a tenant once it becomes active — <paramref name="services" /> lets it resolve its
/// own dependencies (e.g. a tenant-scoped repository) the same way it would from a controller.
/// </param>
/// <param name="onTenantStopped">Runs once for a tenant after it's removed.</param>
sealed class MultiTenantRunnerHostedService(
    IServiceProvider services,
    Func<IServiceProvider, TenantId, CancellationToken, Task> onTenantStarted,
    Func<IServiceProvider, TenantId, CancellationToken, Task> onTenantStopped) : BackgroundService
{
    readonly IServiceProvider _services = services ?? throw new ArgumentNullException(nameof(services));
    readonly Func<IServiceProvider, TenantId, CancellationToken, Task> _onTenantStarted = onTenantStarted ?? throw new ArgumentNullException(nameof(onTenantStarted));
    readonly Func<IServiceProvider, TenantId, CancellationToken, Task> _onTenantStopped = onTenantStopped ?? throw new ArgumentNullException(nameof(onTenantStopped));

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runner = _services.GetRequiredService<MultiTenantRunner>();

        return runner.RunAsync(
            (tenantId, ct) => _onTenantStarted(_services, tenantId, ct),
            (tenantId, ct) => _onTenantStopped(_services, tenantId, ct),
            stoppingToken);
    }
}
