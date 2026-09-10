namespace Cntryl.Portia;

/// <summary>Descriptive information supplied by the receiving transport.</summary>
public abstract record RequestInvocation
{
    /// <summary>Gets the stable, low-cardinality transport name used by telemetry.</summary>
    public abstract string TransportName { get; }
}
