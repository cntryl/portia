namespace Cntryl.Portia;

/// <summary>Implemented by a projector's constructor-injected repository to coordinate data and progress.</summary>
public interface IProjectionStore
{
    /// <summary>Loads the checkpoint committed with this projection's data.</summary>
    /// <param name="identity">The projection whose progress is read.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The committed checkpoint, or the origin when the projection has never committed.</returns>
    ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct = default);

    /// <summary>
    ///     Begins an atomic unit of work on this repository. Implementations may open a transaction
    ///     or buffer writes. Repository methods participate in this unit until its handle is disposed.
    /// </summary>
    /// <param name="context">The projection identity and the progress the batch starts from.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A handle owning the unit of work until it is committed or disposed.</returns>
    ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default);
}
