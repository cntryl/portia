using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
/// Hosts a <see cref="BaseReactor" /> as a continuous background loop: loads its durable checkpoint,
/// runs one <see cref="ReactorRunner" /> pass, persists whatever checkpoint that pass reaches,
/// waits, and repeats — reloading progress after faults until the host shuts down. Register via
/// <c>IServiceCollection.AddPortiaReactorRunner&lt;TReactor&gt;()</c>
/// (<c>Portia.DependencyInjection</c>) rather than constructing this directly.
/// </summary>
public sealed class ReactorHostedService : BackgroundService
{
    const int DefaultMaxBatchSize = 512;

    readonly ReactorRunner _runner;
    readonly BaseReactor _reactor;
    readonly IProjectionCheckpointStore _checkpointStore;
    readonly int _maxBatchSize;
    readonly TimeSpan _pollInterval;
    readonly ILogger<ReactorHostedService>? _logger;

    /// <summary>
    /// Creates a hosted reactor with bounded durable checkpoint batches.
    /// </summary>
    /// <param name="runner">Runs one pass over currently readable events.</param>
    /// <param name="reactor">The reactor to host.</param>
    /// <param name="maxBatchSize">How many events to process between checkpoint saves.</param>
    /// <param name="pollInterval">How long to wait between passes once caught up.</param>
    /// <param name="logger">Reports a faulted pass when supplied.</param>
    public ReactorHostedService(
        ReactorRunner runner,
        BaseReactor reactor,
        int maxBatchSize = DefaultMaxBatchSize,
        TimeSpan? pollInterval = null,
        ILogger<ReactorHostedService>? logger = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _reactor = reactor ?? throw new ArgumentNullException(nameof(reactor));
        _checkpointStore = reactor.Checkpoints;

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchSize);

        _maxBatchSize = maxBatchSize;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_pollInterval, TimeSpan.Zero, nameof(pollInterval));
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ProjectionCheckpoint? checkpoint = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var operation = "checkpoint load";

            try
            {
                checkpoint ??= await _checkpointStore
                    .LoadAsync(new CheckpointIdentity(_reactor.Name, _reactor.Pattern), stoppingToken)
                    .ConfigureAwait(false);

                operation = "pass";

                // Passing _checkpointStore through means progress is durably saved every batch,
                // not only once this whole pass finishes — a mid-pass failure only loses the
                // current batch, not everything back to this call's starting checkpoint.
                var next = await _runner
                    .RunAsync(_reactor, checkpoint.Value, _maxBatchSize, stoppingToken)
                    .ConfigureAwait(false);

                checkpoint = next;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PortiaTelemetry.RecordRunnerFault(
                    nameof(ReactorHostedService),
                    $"reactor '{_reactor.Name}' {operation} faulted",
                    ex,
                    _logger);

                // RunAsync can fault after already durably saving one or more batches internally
                // — this loop's own local `checkpoint` only advances on a *successful* return, so
                // discard it after every fault. The next polling iteration must successfully
                // reload durable progress before another pass can run. A reload failure remains
                // inside this same backoff/retry loop instead of terminating the hosted service.
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
