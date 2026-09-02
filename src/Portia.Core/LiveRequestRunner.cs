namespace Cntryl.Portia;

/// <summary>
/// Dispatches requests delivered live (Fitz notice fanout, a fired Fitz schedule entry) to the
/// request bus, so a command's handler is the same regardless of origin. There is no queueing
/// or redelivery for this transport shape — a failed dispatch is simply lost, not retried.
/// </summary>
/// <param name="consumer">The live-delivery consumer.</param>
/// <param name="bus">The request bus.</param>
/// <param name="actorValidator">Re-validates each request's carried actor token — signature and
/// expiry included — at the moment it's actually delivered.</param>
public sealed class LiveRequestRunner(ILiveRequestConsumer consumer, IRequestBus bus, IRequestActorValidator actorValidator)
{
    readonly ILiveRequestConsumer _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
    readonly IRequestBus _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    readonly IRequestActorValidator _actorValidator = actorValidator ?? throw new ArgumentNullException(nameof(actorValidator));

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
                var actorResult = await _actorValidator.ValidateAsync(delivered.ActorToken, ct).ConfigureAwait(false);

                if (actorResult is not { IsSuccess: true, Value: { } actor })
                {
                    PortiaTelemetry.RecordRunnerFault(nameof(LiveRequestRunner), "actor validation failed");
                    continue;
                }

                // The result's failure category can't change anything at this transport's
                // level (no ack/redelivery); an unrecognized exception is still caught below.
                _ = await _bus.SendAsync(delivered.Request, actor, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Nothing to abandon or redeliver at this transport's level; a failed dispatch
                // is simply lost. Continue processing later deliveries.
                PortiaTelemetry.RecordRunnerFault(nameof(LiveRequestRunner), "unrecognized exception", ex);
            }
        }
    }
}
