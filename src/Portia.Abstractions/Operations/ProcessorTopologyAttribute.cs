namespace Cntryl.Portia;

/// <summary>Generated dispatch metadata in selection order; application overrides have unknown coverage.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ProcessorTopologyAttribute(params string[] subscriptions) : Attribute
{
    /// <summary>Gets generated handler subscriptions as event type and single/batch mode.</summary>
    public IReadOnlyList<string> Subscriptions { get; } = subscriptions;
}
