namespace Cntryl.Portia;

/// <summary>A live notice delivery at its concrete route.</summary>
/// <param name="Route">The concrete route the notice was delivered on.</param>
public sealed record NoticeInvocation(string Route) : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "notice";

    /// <inheritdoc />
    public override RequestTraceRelationship TraceRelationship => RequestTraceRelationship.Link;
}
