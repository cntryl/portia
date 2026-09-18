using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
///     Runs a <see cref="MultiTenantRunner" /> for the life of the host and creates one dependency-
///     injection scope per active tenant. Registered by
///     <see cref="PortiaHostingServiceCollectionExtensions.AddPortiaMultiTenantRunner" /> — not meant
///     to be constructed directly.
/// </summary>
/// <typeparam name="TWorkload">The typed workload resolved inside each tenant scope.</typeparam>
/// <param name="runner">The tenant lifecycle runner.</param>
/// <param name="scopeFactory">Creates one scope per active tenant.</param>
/// <param name="validations">Delays work until configured HTTP endpoint validation completes.</param>
sealed class MultiTenantRunnerHostedService<TWorkload>(
    MultiTenantRunner runner,
    IServiceScopeFactory scopeFactory,
    PortiaStartupValidationRegistry? validations = null) : BackgroundService
    where TWorkload : class, ITenantWorkload
{
    readonly MultiTenantRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (validations is not null)
            await validations.WaitForEndpointValidationsAsync(stoppingToken).ConfigureAwait(false);
        await _runner.RunAsync(RunTenantAsync, static (_, _) => Task.CompletedTask, stoppingToken)
            .ConfigureAwait(false);
    }

    async Task RunTenantAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var workload = scope.ServiceProvider.GetRequiredService<TWorkload>();
        await workload.RunAsync(tenantId, ct).ConfigureAwait(false);
    }
}
