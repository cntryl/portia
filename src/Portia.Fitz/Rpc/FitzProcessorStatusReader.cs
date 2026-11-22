using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

sealed class FitzProcessorStatusReader(
    FitzApplicationConnection connection,
    IServiceScopeFactory scopes,
    RequestTransportCatalog catalog,
    Func<string> application,
    ILogger<FitzProcessorStatusReader>? logger,
    TimeProvider clock) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        FitzApplicationWorkers.RetryAsync(nameof(FitzProcessorStatusReader), ServeAsync, TimeSpan.FromSeconds(1),
            clock, logger, stoppingToken);

    async Task ServeAsync(CancellationToken ct)
    {
        await connection.StartAsync(ct).ConfigureAwait(false);
        var route = FitzProcessorStatusRoutes.ReaderRoute(application());
        await using var reader = await new FitzRpcRequestServer(connection.Client.Rpc, scopes, catalog)
            .RegisterAsync<GetProcessorStatus, ProcessorStatus>(route,
                (_, received) => !ct.IsCancellationRequested && received == route, ct).ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
    }
}

sealed class FitzProcessorOwnerProbe(FitzApplicationConnection connection, FleetRunOptions? fleet, TimeProvider clock)
    : IProcessorOwnerProbe
{
    public async ValueTask<ProcessorOwnerStatus> ObserveAsync(WorkloadIdentity identity, CancellationToken ct)
    {
        if (fleet is null)
            return new ProcessorOwnerStatus(null, clock.GetUtcNow(), "unavailable");

        var lease = await connection.Client.Lease.QueryAsync(FitzWorkloadCoordinator.PartitionRoute(fleet, identity), ct)
            .ConfigureAwait(false);
        return new ProcessorOwnerStatus(lease.IsHeld, clock.GetUtcNow(), "fitz_lease");
    }
}
