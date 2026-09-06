namespace Cntryl.Portia;

/// <summary>
/// Persists a reactor's checkpoint between passes. Projector checkpoints instead belong to their
/// <see cref="IProjectionTarget{TProjection}" /> so projection changes and progress share one
/// atomic commit.
/// </summary>
public interface IProjectionCheckpointStore
{
    /// <summary>
    /// Loads the checkpoint to resume from, or the zero checkpoint if none has been saved yet.
    /// </summary>
    /// <param name="identity">The component and canonical pattern; reactors have no rebuild ID.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The checkpoint to resume from.</returns>
    ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default);

    /// <summary>
    /// Saves the checkpoint reached after a pass.
    /// </summary>
    /// <param name="identity">The component and canonical pattern; reactors have no rebuild ID.</param>
    /// <param name="checkpoint">The checkpoint to save.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the save.</returns>
    ValueTask SaveAsync(CheckpointIdentity identity, ProjectionCheckpoint checkpoint, CancellationToken ct = default);
}
