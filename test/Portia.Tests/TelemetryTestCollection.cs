namespace Cntryl.Portia;

/// <summary>Serializes tests that attach listeners to Portia's process-wide diagnostic sources.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryTestGroup
{
    /// <summary>The shared collection name.</summary>
    public const string Name = "Portia telemetry listeners";
}
