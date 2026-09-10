namespace Cntryl.Portia;

/// <summary>
///     Everything an inbound transport knows about one delivery apart from the request itself: who
///     claims to have sent it, the logical identity to preserve, the ingress facts, and the clock to
///     run it under. Grouping these keeps <see cref="RequestDispatch" />'s two entry points to four
///     parameters instead of nine, and gives an adapter one value to build rather than an argument
///     list to keep in the right order.
/// </summary>
/// <param name="Name">
///     The request's stable wire name — its <see cref="DiscriminatorAttribute" />
///     name where the transport resolved one. This is what reaches telemetry, so it must not be
///     derived from a CLR type name that can be renamed without changing the contract.
/// </param>
/// <param name="Invocation">The concrete inbound transport facts.</param>
/// <param name="Metadata">The validated logical request identity.</param>
/// <param name="ActorToken">
///     The opaque actor token received by the transport, or
///     <see langword="null" /> for an unauthenticated delivery.
/// </param>
/// <param name="TraceContext">The optional W3C context received with the request.</param>
/// <param name="TimeProvider">The optional clock used by the request context.</param>
public sealed record RequestDelivery(
    string Name,
    RequestInvocation Invocation,
    RequestMetadata Metadata,
    string? ActorToken = null,
    RequestTraceContext? TraceContext = null,
    TimeProvider? TimeProvider = null)
{
    /// <summary>
    ///     Builds a delivery, falling back to the request's CLR type name when the transport did not
    ///     resolve a wire name. The fallback lives here so every adapter degrades the same way, and
    ///     so a request that genuinely has no declared discriminator (an in-process-only request that
    ///     reached a transport) still reports something rather than nothing.
    /// </summary>
    /// <param name="request">The delivered request, used only for the name fallback.</param>
    /// <param name="name">The wire name the transport resolved, or <see langword="null" />.</param>
    /// <param name="invocation">The concrete inbound transport facts.</param>
    /// <param name="metadata">The validated logical request identity.</param>
    /// <param name="actorToken">The opaque actor token received by the transport.</param>
    /// <param name="traceContext">The optional W3C context received with the request.</param>
    /// <param name="timeProvider">The optional clock used by the request context.</param>
    /// <returns>The delivery, named by the transport or by the request's CLR type.</returns>
    public static RequestDelivery For(IRequestBase request, string? name, RequestInvocation invocation,
        RequestMetadata metadata, string? actorToken = null, RequestTraceContext? traceContext = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new RequestDelivery(name ?? request.GetType().Name, invocation, metadata, actorToken, traceContext,
            timeProvider);
    }
}
