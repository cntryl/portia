using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
/// Dispatches one-way request notifications (Fitz notice fanout, a fired Fitz schedule entry) to
/// the request bus, so a command's handler is the same regardless of origin. There is no queueing
/// or redelivery for this transport shape — a failed dispatch is simply lost, not retried.
/// </summary>
/// <param name="consumer">The request-notification consumer.</param>
/// <param name="bus">The request bus.</param>
/// <param name="actorValidator">Re-validates each request's carried actor token — signature and
/// expiry included — at the moment it's actually delivered.</param>
/// <param name="logger">
/// Reports a lost delivery even when nothing is listening to
/// <see cref="PortiaTelemetry.ActivitySource" />. Supply it explicitly, or configure
/// Microsoft.Extensions.Logging with at least one provider before resolving the runner through
/// DI; a bare <c>ServiceCollection</c> registration does not create or emit logs.
/// </param>
public sealed class RequestNotificationRunner(
    IRequestNotificationConsumer consumer,
    IRequestBus bus,
    IRequestActorValidator actorValidator,
    ILogger<RequestNotificationRunner>? logger = null)
{
    readonly IRequestNotificationConsumer _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
    readonly IRequestBus _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    readonly IRequestActorValidator _actorValidator = actorValidator ?? throw new ArgumentNullException(nameof(actorValidator));
    readonly ILogger<RequestNotificationRunner>? _logger = logger;

    /// <summary>
    /// Reads and dispatches requests as they are delivered, until cancellation is requested.
    /// A failed dispatch does not stop the run; the request is simply lost, matching this
    /// transport's no-redelivery guarantee. That includes a request whose carried actor token
    /// fails re-validation (e.g. it has expired since it was scheduled or published).
    /// </summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the run.</returns>
    public async Task RunAsync(CancellationToken ct = default)
    {
        await foreach (var delivered in _consumer.ReadAsync(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            try
            {
                // The result's failure category can't change anything at this transport's level
                // (no ack/redelivery). An unrecognized exception is still caught below.
                var dispatch = await RequestDispatch.SendAsync(
                    _actorValidator, _bus, delivered.Request, delivered.ActorToken, ct).ConfigureAwait(false);

                if (!dispatch.WasDispatched)
                    PortiaTelemetry.RecordRunnerFault(nameof(RequestNotificationRunner), "actor validation failed", logger: _logger);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Nothing to abandon or redeliver at this transport's level; a failed dispatch
                // is simply lost. Continue processing later deliveries.
                PortiaTelemetry.RecordRunnerFault(nameof(RequestNotificationRunner), "unrecognized exception", ex, _logger);
            }
        }
    }
}
