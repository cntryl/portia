using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
/// Consumes requests off a queue and dispatches each to the request bus, so a command's handler
/// is the same regardless of whether it originated over HTTP, a queue, or RPC.
/// </summary>
/// <param name="consumer">The queue consumer.</param>
/// <param name="bus">The request bus.</param>
/// <param name="actorValidator">Re-validates each request's carried actor token — signature and
/// expiry included — at the moment it's actually dequeued, not just at the moment it was
/// enqueued.</param>
/// <param name="logger">
/// Reports a dropped or abandoned request even when nothing is listening to
/// <see cref="PortiaTelemetry.ActivitySource" />. Supply it explicitly, or configure
/// Microsoft.Extensions.Logging with at least one provider before resolving the runner through
/// DI; a bare <c>ServiceCollection</c> registration does not create or emit logs.
/// </param>
public sealed class QueueRunner(
    IRequestQueueConsumer consumer,
    IRequestBus bus,
    IRequestActorValidator actorValidator,
    ILogger<QueueRunner>? logger = null)
{
    readonly IRequestQueueConsumer _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
    readonly IRequestBus _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    readonly IRequestActorValidator _actorValidator = actorValidator ?? throw new ArgumentNullException(nameof(actorValidator));
    readonly ILogger<QueueRunner>? _logger = logger;

    /// <summary>
    /// Reserves and dispatches queued requests until the queue is exhausted or cancellation is
    /// requested. A request is completed after its handler succeeds, or after it fails with a
    /// non-transient error (bad input, unauthorized, a conflict — the request would fail
    /// identically on redelivery, so it is dropped, not retried). That includes actor
    /// re-validation failing (e.g. an expired token) — retrying won't make an expired token
    /// valid, so the request is dropped rather than redelivered forever. Any other failure — a
    /// transient error, or an unrecognized (unexpected, infrastructure-level) exception —
    /// abandons the reservation so the request is redelivered.
    /// </summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the run.</returns>
    public async Task RunAsync(CancellationToken ct = default)
    {
        await foreach (var queued in _consumer.ReadAsync(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            try
            {
                var actorResult = await _actorValidator.ValidateAsync(queued.ActorToken, ct).ConfigureAwait(false);

                if (actorResult is not { IsSuccess: true, Value: { } actor })
                {
                    PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), "actor validation failed", logger: _logger);
                    await queued.CompleteAsync(ct).ConfigureAwait(false);
                    continue;
                }

                var result = await _bus.SendAsync(queued.Request, actor, ct).ConfigureAwait(false);

                if (result.IsSuccess || result.Error is { IsTransient: false })
                    await queued.CompleteAsync(ct).ConfigureAwait(false);
                else
                    await queued.AbandonAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // An unrecognized exception's retriability is unknown; abandoning (rather than
                // silently dropping the request) is the safer default.
                PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), "unrecognized exception", ex, _logger);
                await queued.AbandonAsync(ct).ConfigureAwait(false);
            }
        }
    }
}
