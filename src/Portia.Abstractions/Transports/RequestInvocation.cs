namespace Cntryl.Portia;

/// <summary>Descriptive information supplied by the receiving transport.</summary>
public abstract record RequestInvocation
{
    /// <summary>Gets the stable, low-cardinality transport name used by telemetry.</summary>
    public abstract string TransportName { get; }

    /// <summary>Gets how propagated producer context relates to this inbound delivery.</summary>
    public abstract RequestTraceRelationship TraceRelationship { get; }

    /// <summary>Gets the standard messaging system name when the adapter knows it.</summary>
    public virtual string? MessagingSystem { get; init; }
}
