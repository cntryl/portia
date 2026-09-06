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
    /// <see cref="ILeaseClient" /> (or a custom <see cref="IPartitionLeaseCompetitor" />) and
    /// <typeparamref name="TWorkload" /> to already be registered. A fresh dependency-injection
    /// scope and workload instance are used for every held lease.
    /// </summary>
    /// <typeparam name="TWorkload">The partition-scoped component to run.</typeparam>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="partitions">The fixed, deployment-time-known set of partition routes.</param>
    /// <param name="options">How long a held lease survives without renewal.</param>
    /// <returns><paramref name="services" />, for chaining.</returns>
    public static IServiceCollection AddPortiaFleetPartitionRunner<TWorkload>(
        this IServiceCollection services,
        IReadOnlyCollection<string> partitions,
        FleetRunOptions options)
        where TWorkload : class, IPartitionWorkload
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(partitions);

        ArgumentNullException.ThrowIfNull(options);
        options.Validate(partitions);
        partitions = [.. partitions];
        services.TryAddSingleton<IFleetMembership, FitzFleetMembership>();
        services.TryAddSingleton<IPartitionLeaseCompetitor>(sp => new FitzPartitionLeaseCompetitor(sp.GetRequiredService<ILeaseClient>()));
        services.TryAddSingleton<FleetPartitionRunner>();
        _ = services.AddHostedService(sp => new FleetPartitionRunnerHostedService<TWorkload>(
            sp.GetRequiredService<FleetPartitionRunner>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            partitions,
            options));
        return services;
    }
}
