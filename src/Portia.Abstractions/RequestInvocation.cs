namespace Cntryl.Portia;

/// <summary>Descriptive information supplied by the receiving transport.</summary>
public abstract record RequestInvocation;
/// <summary>An in-process call.</summary>
public sealed record DirectInvocation : RequestInvocation;
/// <summary>An HTTP invocation, excluding credentials, headers, and the query string.</summary>
public sealed record HttpInvocation(string Method, string Path, string? RoutePattern, string RequestIdentifier) : RequestInvocation;
/// <summary>An RPC invocation at its concrete received route.</summary>
public sealed record RpcInvocation(string Route) : RequestInvocation;
/// <summary>A queue delivery with its concrete route and broker-reported attempt.</summary>
public sealed record QueueInvocation(string Route, uint Attempt) : RequestInvocation;
/// <summary>A live notice delivery at its concrete route.</summary>
public sealed record NoticeInvocation(string Route) : RequestInvocation;
/// <summary>A schedule delivery; the current transport provides no occurrence identity.</summary>
public sealed record ScheduleInvocation(string Route) : RequestInvocation;
