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

    /// <summary>
    /// Gets the one-based number of times this request has been delivered, including this
    /// delivery.
    /// </summary>
    uint Attempt { get; }

    /// <summary>
    /// Gets the raw bearer token of the actor that enqueued this request, or
    /// <see langword="null" /> for an unauthenticated actor. Re-validate this (via
    /// <see cref="IRequestActorValidator" />) rather than trusting it as-is — it may have expired
    /// since it was enqueued.
    /// </summary>
    string? ActorToken { get; }

    /// <summary>
    /// Marks the request as successfully handled, so it is not redelivered.
    /// </summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the completion.</returns>
    ValueTask CompleteAsync(CancellationToken ct = default);

    /// <summary>
    /// Releases the request back to the queue for redelivery, without waiting for its
    /// reservation to expire.
    /// </summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the abandonment.</returns>
    ValueTask AbandonAsync(CancellationToken ct = default);
}
