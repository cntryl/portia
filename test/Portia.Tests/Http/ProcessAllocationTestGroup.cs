namespace Cntryl.Portia;

/// <summary>Isolates process-wide allocation measurements from other test workloads.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessAllocationTestGroup
{
    /// <summary>The shared collection name.</summary>
    public const string Name = "Portia process allocation";
}
