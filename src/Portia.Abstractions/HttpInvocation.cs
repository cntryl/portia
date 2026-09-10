namespace Cntryl.Portia;

/// <summary>An HTTP invocation, excluding credentials, headers, and the query string.</summary>
public sealed record HttpInvocation(string Method, string Path, string? RoutePattern, string RequestIdentifier)
    : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "http";
}
