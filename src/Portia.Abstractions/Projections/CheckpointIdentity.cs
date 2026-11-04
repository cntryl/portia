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
    /// <exception cref="ArgumentException">
    ///     A name or generation is blank or contains NUL or malformed UTF-16 text.
    /// </exception>
    public CheckpointIdentity(string componentName, EventStreamPattern pattern, string? rebuildId = null)
    {
        _ = IdentityText.Require(componentName, nameof(componentName));
        ArgumentNullException.ThrowIfNull(pattern);
        if (rebuildId is not null)
        {
            _ = IdentityText.Require(rebuildId, nameof(rebuildId));
        }

        ComponentName = componentName;
        Pattern = pattern;
        RebuildId = rebuildId;
    }

    /// <summary>Gets the stable component name.</summary>
    public string ComponentName { get; }

    /// <summary>Gets the structural stream selector.</summary>
    public EventStreamPattern Pattern { get; }

    /// <summary>Gets the rebuild generation, or null for live processing.</summary>
    public string? RebuildId { get; }
}
