namespace Cntryl.Portia;

/// <summary>Descriptive information supplied by the receiving transport.</summary>
public abstract record RequestInvocation
{
    /// <summary>Gets the stable, low-cardinality transport name used by telemetry.</summary>
    public abstract string TransportName { get; }
}
/// <summary>An in-process call.</summary>
public sealed record DirectInvocation : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "local";
}
/// <summary>An HTTP invocation, excluding credentials, headers, and the query string.</summary>
public sealed record HttpInvocation(string Method, string Path, string? RoutePattern, string RequestIdentifier) : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "http";
}
/// <summary>An RPC invocation at its concrete received route.</summary>
public sealed record RpcInvocation(string Route) : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "rpc";
}
/// <summary>A queue delivery with its concrete route and broker-reported attempt.</summary>
public sealed record QueueInvocation(string Route, uint Attempt) : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "queue";
}
/// <summary>A live notice delivery at its concrete route.</summary>
public sealed record NoticeInvocation(string Route) : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "notice";
}
/// <summary>A schedule delivery; the current transport provides no occurrence identity.</summary>
public sealed record ScheduleInvocation(string Route) : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "schedule";
}
