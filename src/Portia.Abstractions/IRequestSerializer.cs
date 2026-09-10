namespace Cntryl.Portia;

/// <summary>
/// Serializes concrete requests for an out-of-process transport.
/// </summary>
public interface IRequestSerializer
{
    /// <summary>Serializes logical identity and optional W3C trace context.</summary>
    /// <param name="request">The concrete request payload.</param>
    /// <param name="actorToken">The opaque actor token to revalidate at the receiving boundary.</param>
    /// <param name="metadata">The validated logical request identity to propagate.</param>
    /// <param name="traceContext">The optional W3C trace fields to propagate.</param>
    /// <returns>The transport wire representation.</returns>
    ReadOnlyMemory<byte> Serialize(IRequestBase request, string? actorToken, RequestMetadata metadata, RequestTraceContext? traceContext);
}

/// <summary>
/// Deserializes concrete requests received from an out-of-process transport.
/// </summary>
public interface IRequestDeserializer
{
    /// <summary>Reads the request, opaque actor token, logical metadata, and optional trace context.</summary>
    /// <param name="data">The transport wire representation.</param>
    /// <returns>The fully deserialized request envelope.</returns>
    DeserializedRequest DeserializeEnvelope(ReadOnlyMemory<byte> data);
}

/// <summary>
/// Serializes request outcomes returned by an out-of-process handler.
/// </summary>
public interface IRequestOutcomeSerializer
{
    /// <summary>
    /// Serializes the outcome of handling a no-result request.
    /// </summary>
    /// <param name="outcome">The outcome to serialize.</param>
    /// <returns>The wire representation.</returns>
    ReadOnlyMemory<byte> SerializeOutcome(Result outcome);

    /// <summary>
    /// Serializes the outcome of handling a request that produces a result.
    /// </summary>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    /// <param name="result">The outcome to serialize.</param>
    /// <returns>The wire representation.</returns>
    ReadOnlyMemory<byte> SerializeResult<TOut>(Result<TOut> result);
}

/// <summary>
/// Deserializes request outcomes received by an out-of-process caller.
/// </summary>
public interface IRequestOutcomeDeserializer
{
    /// <summary>
    /// Deserializes the outcome of handling a no-result request.
    /// </summary>
    /// <param name="data">The wire representation.</param>
    /// <returns>The deserialized outcome.</returns>
    Result DeserializeOutcome(ReadOnlyMemory<byte> data);

    /// <summary>
    /// Deserializes the outcome of handling a request that produces a result.
    /// </summary>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    /// <param name="data">The wire representation.</param>
    /// <returns>The deserialized outcome.</returns>
    Result<TOut> DeserializeResult<TOut>(ReadOnlyMemory<byte> data);
}

/// <summary>Represents a deserialized transport request envelope.</summary>
/// <param name="Request">The concrete request payload.</param>
/// <param name="Name">The stable wire name the envelope declared, independent of the CLR type name.</param>
/// <param name="ActorToken">The opaque actor token to revalidate before dispatch.</param>
/// <param name="Metadata">The validated propagated logical request identity.</param>
/// <param name="TraceContext">The optional propagated W3C trace fields.</param>
public sealed record DeserializedRequest(IRequestBase Request, string Name, string? ActorToken, RequestMetadata Metadata,
    RequestTraceContext? TraceContext = null)
{
    /// <summary>Builds the transport-neutral delivery this envelope describes.</summary>
    /// <param name="invocation">The concrete inbound transport facts.</param>
    /// <param name="timeProvider">The optional clock used by the request context.</param>
    public RequestDelivery ToDelivery(RequestInvocation invocation, TimeProvider? timeProvider = null) =>
        RequestDelivery.For(Request, Name, invocation, Metadata, ActorToken, TraceContext, timeProvider);
}
