namespace Cntryl.Portia;

/// <summary>
/// Describes one projector batch without exposing its target datastore.
/// </summary>
/// <param name="ProjectorName">The stable projector name.</param>
/// <param name="Pattern">The event-stream pattern being projected.</param>
/// <param name="Checkpoint">The checkpoint at which the batch begins.</param>
/// <param name="IsRebuild">Whether the batch belongs to a rebuild generation.</param>
public sealed record ProjectionBatchContext(
    string ProjectorName,
    EventStreamPattern Pattern,
    ProjectionCheckpoint Checkpoint,
    bool IsRebuild);
