namespace Cntryl.Portia;

/// <summary>An HTTP invocation, excluding credentials, headers, and the query string.</summary>
/// <param name="Method">The HTTP method the request arrived with.</param>
/// <param name="Path">The concrete request path.</param>
/// <param name="RoutePattern">
///     The matched route template, or <see langword="null" /> when the host
///     did not resolve one.
/// </param>
/// <param name="RequestIdentifier">The host's identifier for this HTTP request.</param>
public sealed record HttpInvocation(string Method, string Path, string? RoutePattern, string RequestIdentifier)
    : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "http";

    /// <inheritdoc />
    public override RequestTraceRelationship TraceRelationship => RequestTraceRelationship.Parent;
}
