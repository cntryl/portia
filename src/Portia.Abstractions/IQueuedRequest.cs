namespace Cntryl.Portia;

/// <summary>
/// A request reserved off a queue, independent of which queue technology delivered it.
/// </summary>
public interface IQueuedRequest
{
    /// <summary>
    /// Gets the reserved request.
    /// </summary>
    IRequest Request { get; }

    /// <summary>Gets the logical identity preserved across deliveries.</summary>
    RequestMetadata Metadata { get; }

    /// <summary>Gets the concrete ingress facts supplied by this transport.</summary>
    RequestInvocation Invocation { get; }

    /// <summary>
    /// Gets the transport-reported delivery attempt. Portia does not synthesize a retry counter.
    /// </summary>
    uint Attempt { get; }

    /// <summary>
    /// Gets the raw bearer token of the actor that enqueued this request, or
    /// <see langword="null" /> for an unauthenticated actor. Re-validate this (via
    /// <see cref="IRequestActorValidator" />) rather than trusting it as-is — it may have expired
    /// since it was enqueued.
    /// </summary>
    string? ActorToken { get; }

    /// <summary>Signals that the transport can no longer maintain this reservation.</summary>
    CancellationToken ReservationCancellation => CancellationToken.None;

    /// <summary>
    /// Marks the request as successfully handled, so it is not redelivered.
    /// </summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the completion.</returns>
    ValueTask CompleteAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops processing without acknowledging the request. The transport owns redelivery;
    /// Fitz waits for reservation expiration according to broker configuration.
    /// </summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the abandonment.</returns>
    ValueTask AbandonAsync(CancellationToken ct = default);
}
