namespace Cntryl.Portia;

/// <summary>Represents a deserialized transport request envelope.</summary>
/// <param name="Request">The concrete request payload.</param>
/// <param name="Name">The stable wire name the envelope declared, independent of the CLR type name.</param>
/// <param name="ActorToken">The opaque actor token to revalidate before dispatch.</param>
/// <param name="Metadata">The validated propagated logical request identity.</param>
/// <param name="TraceContext">The optional propagated W3C trace fields.</param>
public sealed record DeserializedRequest(
    IRequestBase Request,
    string Name,
    string? ActorToken,
    RequestMetadata Metadata,
    RequestTraceContext? TraceContext = null)
{
    /// <summary>Builds the transport-neutral delivery this envelope describes.</summary>
    /// <param name="invocation">The concrete inbound transport facts.</param>
    /// <param name="timeProvider">The optional clock used by the request context.</param>
    /// <returns>The delivery describing this envelope.</returns>
    public RequestDelivery ToDelivery(RequestInvocation invocation, TimeProvider? timeProvider = null) =>
        RequestDelivery.For(Request, Name, invocation, Metadata, ActorToken, TraceContext, timeProvider);
}
