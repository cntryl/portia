using Cntryl.Fitz.Abstractions.Domains.Lease;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
/// Runs a <see cref="FleetPartitionRunner" /> for the life of the host, resolving it from
/// <paramref name="services" /> lazily (at <see cref="ExecuteAsync" /> time, not construction
/// time) so its own dependencies don't have to be resolvable before hosted services start.
/// Registered by <see cref="PortiaFitzHostingServiceCollectionExtensions.AddPortiaFleetPartitionRunner" />
/// — not meant to be constructed directly.
/// </summary>
/// <param name="services">The container the runner and per-partition callback resolve through.</param>
/// <param name="partitions">The fixed, deployment-time-known set of partition routes.</param>
/// <param name="onPartitionAcquired">
/// Runs while this worker holds a partition's lease — <paramref name="services" /> lets it
/// resolve its own dependencies the same way it would from a controller.
/// </param>
/// <param name="leaseTtl">How long a held lease survives without renewal.</param>
sealed class FleetPartitionRunnerHostedService(
    IServiceProvider services,
    IReadOnlyCollection<string> partitions,
    Func<IServiceProvider, string, LeaseAuthority, CancellationToken, Task> onPartitionAcquired,
    TimeSpan leaseTtl) : BackgroundService
{
    readonly IServiceProvider _services = services ?? throw new ArgumentNullException(nameof(services));
    readonly IReadOnlyCollection<string> _partitions = partitions ?? throw new ArgumentNullException(nameof(partitions));
    readonly Func<IServiceProvider, string, LeaseAuthority, CancellationToken, Task> _onPartitionAcquired = onPartitionAcquired ?? throw new ArgumentNullException(nameof(onPartitionAcquired));
    readonly TimeSpan _leaseTtl = leaseTtl;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runner = _services.GetRequiredService<FleetPartitionRunner>();

        return runner.RunAsync(
            _partitions,
            (partition, authority, ct) => _onPartitionAcquired(_services, partition, authority, ct),
            _leaseTtl,
            stoppingToken);
    }
}
