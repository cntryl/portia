namespace Cntryl.Portia;

/// <summary>
///     Serializes concrete requests for an out-of-process transport.
/// </summary>
public interface IRequestSerializer
{
    /// <summary>Serializes logical identity and optional W3C trace context.</summary>
    /// <param name="request">The concrete request payload.</param>
    /// <param name="actorToken">The opaque actor token to revalidate at the receiving boundary.</param>
    /// <param name="metadata">The validated logical request identity to propagate.</param>
    /// <param name="traceContext">The optional W3C trace fields to propagate.</param>
    /// <returns>The transport wire representation.</returns>
    ReadOnlyMemory<byte> Serialize(IRequestBase request, string? actorToken, RequestMetadata metadata,
        RequestTraceContext? traceContext);
}
