namespace Cntryl.Portia;

/// <summary>Implemented by a projector's constructor-injected repository to coordinate data and progress.</summary>
public interface IProjectionStore
{
    /// <summary>Loads the checkpoint committed with this projection's data.</summary>
    ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct = default);
    /// <summary>Begins an atomic unit of work on this repository. Implementations may open a transaction
    /// or buffer writes. Repository methods participate in this unit until its handle is disposed.</summary>
    ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default);
}
