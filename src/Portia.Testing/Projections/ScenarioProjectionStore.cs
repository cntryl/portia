namespace Cntryl.Portia.Testing;

sealed class ScenarioProjectionStore : IProjectionStore
{
    readonly Dictionary<CheckpointIdentity, ProjectionCheckpoint> _checkpoints = [];

    public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity,
        CancellationToken ct = default) =>
        ValueTask.FromResult(_checkpoints.GetValueOrDefault(identity, ProjectionCheckpoint.Start));

    public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult<IProjectionBatch>(new Batch(this, context.Identity));
    }

    sealed class Batch(ScenarioProjectionStore store, CheckpointIdentity identity) : IProjectionBatch
    {
        public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            store._checkpoints[identity] = checkpoint;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
