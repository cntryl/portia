namespace Cntryl.Portia;

sealed class LateFaultProjectionTarget : ITestProjectionRepository
{
    ProjectionCheckpoint _checkpoint;

    public int CommitAttempts { get; private set; }
    public TestProjection Projection { get; } = new();

    public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(
        CheckpointIdentity identity,
        CancellationToken ct = default) => ValueTask.FromResult(_checkpoint);

    public ValueTask<IProjectionBatch> BeginAsync(
        ProjectionBatchContext context,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IProjectionBatch>(new LateFaultProjectionBatch(this));

    sealed class LateFaultProjectionBatch(LateFaultProjectionTarget target) : IProjectionBatch
    {
        public TestProjection Projection => target.Projection;

        public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
        {
            target._checkpoint = checkpoint;
            var commitAttempt = ++target.CommitAttempts;

            return commitAttempt == 1
                ? ValueTask.FromException(
                    new InvalidOperationException("Simulated late commit acknowledgement failure."))
                : ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
