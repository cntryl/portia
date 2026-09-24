namespace Cntryl.Portia;

/// <summary>
///     A request reserved off a queue, independent of which queue technology delivered it.
/// </summary>
public interface IQueuedRequest
{
    /// <summary>
    ///     Gets the reserved request.
    /// </summary>
    IRequest Request { get; }

    /// <summary>
    ///     Gets the request's stable wire name, or <see langword="null" /> when this queue adapter
    ///     does not resolve one. Portia falls back to the CLR type name only in that case, so an
    ///     adapter that knows the request's <see cref="DiscriminatorAttribute" /> should report it
    ///     and keep telemetry independent of CLR renames.
    /// </summary>
    string? Name => null;

    /// <summary>Gets the logical identity preserved across deliveries.</summary>
    RequestMetadata Metadata { get; }

    /// <summary>Gets the optional propagated W3C trace context for this delivery.</summary>
    /// <remarks>The default preserves compatibility with queue adapters that do not propagate tracing.</remarks>
    RequestTraceContext? TraceContext => null;

    /// <summary>Gets the concrete ingress facts supplied by this transport.</summary>
    RequestInvocation Invocation { get; }

    /// <summary>
    ///     Gets the transport-reported delivery attempt. Portia does not synthesize a retry counter.
    /// </summary>
    uint Attempt { get; }

    /// <summary>
    ///     Gets whether <see cref="Attempt" /> is a durable transport-owned count preserved across
    ///     redeliveries. The default is <see langword="false" />.
    /// </summary>
    bool SupportsDurableAttempts => false;

    /// <summary>
    ///     Gets the raw bearer token of the actor that enqueued this request, or
    ///     <see langword="null" /> for an unauthenticated actor. Re-validate this (via
    ///     <see cref="IRequestActorValidator" />) rather than trusting it as-is — it may have expired
    ///     since it was enqueued.
    /// </summary>
    string? ActorToken { get; }

    /// <summary>Signals that the transport can no longer maintain this reservation.</summary>
    /// <remarks>
    ///     Once it fires the delivery is the transport's again: the runner neither completes nor
    ///     abandons it and reports nothing, so the transport that cancels it reports why.
    /// </remarks>
    CancellationToken ReservationCancellation => CancellationToken.None;

    /// <summary>
    ///     Marks the request as successfully handled, so it is not redelivered.
    /// </summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the completion.</returns>
    ValueTask CompleteAsync(CancellationToken ct = default);

    /// <summary>
    ///     Stops processing without acknowledging the request. The transport owns redelivery: the
    ///     queue technology waits for the reservation to expire according to its own configuration.
    /// </summary>
    /// <remarks>
    ///     Not every unsettled delivery is abandoned. After a lost reservation, or a refused
    ///     acknowledgment of a request that was handled, the runner calls neither this nor
    ///     <see cref="CompleteAsync" />, so a reservation must also be released when it expires.
    /// </remarks>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the abandonment.</returns>
    ValueTask AbandonAsync(CancellationToken ct = default);
}
