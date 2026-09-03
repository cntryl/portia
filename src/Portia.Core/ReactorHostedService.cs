using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
/// Hosts a <see cref="Reactor" /> as a continuous background loop: loads its checkpoint once at
/// startup, runs one <see cref="ReactorRunner" /> pass, persists whatever checkpoint that pass
/// reaches, waits, and repeats — until the host shuts down. Register via
/// <c>IServiceCollection.AddPortiaReactorRunner&lt;TReactor&gt;()</c>
/// (<c>Portia.DependencyInjection</c>) rather than constructing this directly.
/// </summary>
/// <param name="runner">Runs one pass over currently readable events.</param>
/// <param name="reactor">The reactor to host.</param>
/// <param name="checkpointStore">Persists the checkpoint between passes.</param>
/// <param name="pollInterval">
/// How long to wait between passes once one catches up to the end of what's currently readable.
/// Defaults to one second.
/// </param>
/// <param name="logger">
/// Reports a faulted pass even when nothing is listening to
/// <see cref="PortiaTelemetry.ActivitySource" />.
/// </param>
public sealed class ReactorHostedService(
    ReactorRunner runner,
    Reactor reactor,
    IProjectionCheckpointStore checkpointStore,
    TimeSpan? pollInterval = null,
    ILogger<ReactorHostedService>? logger = null) : BackgroundService
{
    readonly ReactorRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    readonly Reactor _reactor = reactor ?? throw new ArgumentNullException(nameof(reactor));
    readonly IProjectionCheckpointStore _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
    readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
    readonly ILogger<ReactorHostedService>? _logger = logger;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkpoint = await _checkpointStore.LoadAsync(_reactor.Name, stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var next = await _runner.RunAsync(_reactor, checkpoint, stoppingToken).ConfigureAwait(false);

                if (next != checkpoint)
                {
                    await _checkpointStore.SaveAsync(_reactor.Name, next, stoppingToken).ConfigureAwait(false);
                    checkpoint = next;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PortiaTelemetry.RecordRunnerFault(nameof(ReactorHostedService), $"reactor '{_reactor.Name}' pass faulted", ex, _logger);
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
