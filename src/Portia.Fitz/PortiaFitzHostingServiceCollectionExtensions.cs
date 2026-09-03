using Cntryl.Fitz.Abstractions.Domains.Lease;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia;

/// <summary>
/// Wires <see cref="FleetPartitionRunner" /> into a Microsoft.Extensions.Hosting host, as an
/// <see cref="Microsoft.Extensions.Hosting.IHostedService" /> that starts when the host starts
/// and stops cleanly when it shuts down — the same pattern <c>Portia.DependencyInjection</c>
/// uses for every other runner, kept in <c>Portia.Fitz</c> instead since it depends on
/// <c>Cntryl.Fitz.Abstractions</c>.
/// </summary>
public static class PortiaFitzHostingServiceCollectionExtensions
{
    /// <summary>
    /// Hosts a <see cref="FleetPartitionRunner" /> for the life of the host. Requires
    /// <see cref="ILeaseClient" /> to already be registered.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="partitions">The fixed, deployment-time-known set of partition routes.</param>
    /// <param name="onPartitionAcquired">Runs while this worker holds a partition's lease — see
    /// <see cref="FleetPartitionRunner.RunAsync" />.</param>
    /// <param name="leaseTtl">How long a held lease survives without renewal.</param>
    /// <returns><paramref name="services" />, for chaining.</returns>
    public static IServiceCollection AddPortiaFleetPartitionRunner(
        this IServiceCollection services,
        IReadOnlyCollection<string> partitions,
        Func<IServiceProvider, string, LeaseAuthority, CancellationToken, Task> onPartitionAcquired,
        TimeSpan leaseTtl)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(onPartitionAcquired);

        services.TryAddSingleton<FleetPartitionRunner>();
        _ = services.AddHostedService(sp => new FleetPartitionRunnerHostedService(sp, partitions, onPartitionAcquired, leaseTtl));
        return services;
    }
}
