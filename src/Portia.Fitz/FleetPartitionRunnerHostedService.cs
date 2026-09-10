using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
///     Runs a <see cref="FleetPartitionRunner" /> for the life of the host and creates one dependency-
///     injection scope per held partition lease.
///     Registered by <see cref="PortiaFitzHostingServiceCollectionExtensions.AddPortiaFleetPartitionRunner" />
///     — not meant to be constructed directly.
/// </summary>
/// <typeparam name="TWorkload">The typed workload resolved inside each lease scope.</typeparam>
/// <param name="runner">The partition competition runner.</param>
/// <param name="scopeFactory">Creates one scope per held partition lease.</param>
/// <param name="partitions">The fixed, deployment-time-known set of partition routes.</param>
/// <param name="options">How long a held lease survives without renewal.</param>
/// <param name="applicationLifetime">Stops the host after a terminal partition-shutdown failure.</param>
sealed class FleetPartitionRunnerHostedService<TWorkload>(
    FleetPartitionRunner runner,
    IServiceScopeFactory scopeFactory,
    IReadOnlyCollection<string> partitions,
    FleetRunOptions options,
    IHostApplicationLifetime? applicationLifetime = null) : BackgroundService
    where TWorkload : class, IPartitionWorkload
{
    readonly FleetRunOptions _options = options;

    readonly IReadOnlyCollection<string>
        _partitions = partitions ?? throw new ArgumentNullException(nameof(partitions));

    readonly FleetPartitionRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _runner.RunAsync(_partitions, RunPartitionAsync, _options, stoppingToken).ConfigureAwait(false);
        }
        catch (FleetPartitionTerminationTimeoutException)
        {
            applicationLifetime?.StopApplication();
            throw;
        }
    }

    async Task RunPartitionAsync(string partition, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var workload = scope.ServiceProvider.GetRequiredService<TWorkload>();
        await workload.RunAsync(partition, ct).ConfigureAwait(false);
    }
}
