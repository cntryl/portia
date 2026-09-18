namespace Cntryl.Portia;

/// <summary>Describes the identity and authoritative starting progress for one atomic projection batch.</summary>
/// <param name="Identity">The data and checkpoint storage identity.</param>
/// <param name="Checkpoint">The checkpoint at which the batch begins.</param>
public sealed record ProjectionBatchContext(CheckpointIdentity Identity, ProjectionCheckpoint Checkpoint)
{
    /// <summary>Gets whether this batch writes a rebuild generation.</summary>
    public bool IsRebuild => Identity.RebuildId is not null;
}
