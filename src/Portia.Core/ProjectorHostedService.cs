using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
/// Hosts a <see cref="Projector{TProjection}" /> as a continuous background loop: loads its
/// checkpoint once at startup, runs one <see cref="ProjectorRunner" /> pass, persists whatever
/// checkpoint that pass reaches, waits, and repeats — until the host shuts down. Register via
/// <c>IServiceCollection.AddPortiaProjectorRunner&lt;TProjection&gt;()</c>
/// (<c>Portia.DependencyInjection</c>) rather than constructing this directly.
/// </summary>
/// <typeparam name="TProjection">The projection-specific application port.</typeparam>
/// <param name="runner">Runs one batched pass over currently readable events.</param>
/// <param name="projector">The projector to host.</param>
/// <param name="checkpointStore">Persists the checkpoint between passes.</param>
/// <param name="options">The batching and rebuild options for every pass.</param>
/// <param name="pollInterval">
/// How long to wait between passes once one catches up to the end of what's currently readable.
/// Defaults to one second.
/// </param>
/// <param name="logger">
/// Reports a faulted pass even when nothing is listening to
/// <see cref="PortiaTelemetry.ActivitySource" />.
/// </param>
public sealed class ProjectorHostedService<TProjection>(
    ProjectorRunner runner,
    Projector<TProjection> projector,
    IProjectionCheckpointStore checkpointStore,
    ProjectionRunOptions? options = null,
    TimeSpan? pollInterval = null,
    ILogger<ProjectorHostedService<TProjection>>? logger = null) : BackgroundService
{
    readonly ProjectorRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    readonly Projector<TProjection> _projector = projector ?? throw new ArgumentNullException(nameof(projector));
    readonly IProjectionCheckpointStore _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
    readonly ProjectionRunOptions? _options = options;
    readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
    readonly ILogger<ProjectorHostedService<TProjection>>? _logger = logger;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkpoint = await _checkpointStore.LoadAsync(_projector.Name, stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var next = await _runner.RunAsync(_projector, checkpoint, _options, stoppingToken).ConfigureAwait(false);

                if (next != checkpoint)
                {
                    await _checkpointStore.SaveAsync(_projector.Name, next, stoppingToken).ConfigureAwait(false);
                    checkpoint = next;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PortiaTelemetry.RecordRunnerFault(
                    nameof(ProjectorHostedService<>),
                    $"projector '{_projector.Name}' pass faulted",
                    ex,
                    _logger);
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
