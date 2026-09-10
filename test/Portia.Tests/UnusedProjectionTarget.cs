namespace Cntryl.Portia;

sealed class UnusedProjectionTarget : ITestProjectionRepository
{
    public TestProjection Projection { get; } = new();

    public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(
        CheckpointIdentity identity,
        CancellationToken ct = default) => throw new NotSupportedException();

    public ValueTask<IProjectionBatch> BeginAsync(
        ProjectionBatchContext context,
        CancellationToken ct = default) => throw new NotSupportedException();
}
