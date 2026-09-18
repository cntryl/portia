namespace Cntryl.Portia;

/// <summary>An in-process call.</summary>
public sealed record DirectInvocation : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "local";

    /// <inheritdoc />
    public override RequestTraceRelationship TraceRelationship => RequestTraceRelationship.Parent;
}
