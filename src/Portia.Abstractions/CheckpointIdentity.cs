namespace Cntryl.Portia;

/// <summary>Identifies progress and projection data for one component, canonical stream pattern, and generation.</summary>
public sealed record CheckpointIdentity
{
    /// <summary>Creates a scoped checkpoint identity. Null rebuild IDs select live processing.</summary>
    /// <param name="componentName">The stable name of the projector or reactor that owns the progress.</param>
    /// <param name="pattern">The event streams the component consumes.</param>
    /// <param name="rebuildId">
    ///     The rebuild generation, or <see langword="null" /> for live processing.
    /// </param>
    public CheckpointIdentity(string componentName, EventStreamPattern pattern, string? rebuildId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        ArgumentNullException.ThrowIfNull(pattern);
        if (rebuildId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rebuildId);
        }

        ComponentName = componentName;
        Pattern = pattern.ToString();
        RebuildId = rebuildId;
    }

    /// <summary>Gets the stable component name.</summary>
    public string ComponentName { get; }

    /// <summary>Gets the canonical stream selector.</summary>
    public string Pattern { get; }

    /// <summary>Gets the rebuild generation, or null for live processing.</summary>
    public string? RebuildId { get; }
}
