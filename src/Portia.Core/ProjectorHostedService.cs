using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
/// Hosts a <see cref="Projector{TProjection}" /> as a continuous background loop: loads its
/// checkpoint from its projection target, runs one <see cref="ProjectorRunner" /> pass, waits,
/// and repeats — reloading authoritative progress after faults until the host shuts down. Register via
/// <c>IServiceCollection.AddPortiaProjectorRunner&lt;TProjection&gt;()</c>
/// (<c>Portia.DependencyInjection</c>) rather than constructing this directly.
/// </summary>
/// <typeparam name="TProjection">The projection-specific application port.</typeparam>
/// <param name="runner">Runs one batched pass over currently readable events.</param>
/// <param name="projector">The projector to host.</param>
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
    ProjectionRunOptions? options = null,
    TimeSpan? pollInterval = null,
    ILogger<ProjectorHostedService<TProjection>>? logger = null) : BackgroundService
{
    readonly ProjectorRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    readonly Projector<TProjection> _projector = projector ?? throw new ArgumentNullException(nameof(projector));
    readonly ProjectionRunOptions? _options = options;
    readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
    readonly ILogger<ProjectorHostedService<TProjection>>? _logger = logger;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ProjectionCheckpoint? checkpoint = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var operation = "checkpoint load";

            try
            {
                checkpoint ??= await _projector.Target
                    .LoadCheckpointAsync(_projector.Name, stoppingToken)
                    .ConfigureAwait(false);

                operation = "pass";
                checkpoint = await _runner
                    .RunAsync(_projector, checkpoint.Value, _options, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PortiaTelemetry.RecordRunnerFault(
                    nameof(ProjectorHostedService<>),
                    $"projector '{_projector.Name}' {operation} faulted",
                    ex,
                    _logger);

                // CommitAsync may have made the projection and checkpoint durable before an
                // adapter surfaced a late failure. Discard process-local progress after every
                // fault. The next polling iteration must successfully reload the target's
                // authoritative checkpoint before another pass can run; a failed reload stays
                // inside this same backoff/retry loop and can never fall back to stale state.
                checkpoint = null;
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
