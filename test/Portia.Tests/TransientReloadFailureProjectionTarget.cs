namespace Cntryl.Portia;

sealed class TransientReloadFailureProjectionTarget : ITestProjectionRepository
{
    readonly TaskCompletionSource _checkpointReloaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    ProjectionCheckpoint _checkpoint;

    public int LoadAttempts { get; private set; }

    public int CommitAttempts { get; private set; }

    public Task CheckpointReloaded => _checkpointReloaded.Task;

    public TestProjection Projection { get; } = new();

    public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(
        CheckpointIdentity identity,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        LoadAttempts++;

        if (LoadAttempts == 2)
        {
            return ValueTask.FromException<ProjectionCheckpoint>(
                new InvalidOperationException("Simulated transient checkpoint reload failure."));
        }
        else if (LoadAttempts == 3)
        {
            _ = _checkpointReloaded.TrySetResult();
        }

        return ValueTask.FromResult(_checkpoint);
    }

    public ValueTask<IProjectionBatch> BeginAsync(
        ProjectionBatchContext context,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IProjectionBatch>(new TransientReloadFailureProjectionBatch(this));

    sealed class TransientReloadFailureProjectionBatch(TransientReloadFailureProjectionTarget target)
        : IProjectionBatch
    {
        public TestProjection Projection => target.Projection;

        public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
        {
            target._checkpoint = checkpoint;
            target.CommitAttempts++;

            return target.CommitAttempts == 1
                ? ValueTask.FromException(
                    new InvalidOperationException("Simulated late commit acknowledgement failure."))
                : ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
