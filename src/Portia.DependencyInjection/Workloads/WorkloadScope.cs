namespace Cntryl.Portia;

/// <summary>The independent execution scope of a registered component.</summary>
public enum WorkloadScope
{
    /// <summary>One logical workload for the application.</summary>
    Global,

    /// <summary>One logical workload per active tenant.</summary>
    PerTenant
}
