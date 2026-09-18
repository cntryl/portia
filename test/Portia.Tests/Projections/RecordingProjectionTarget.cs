namespace Cntryl.Portia;

sealed class RecordingProjectionTarget : ITestProjectionRepository
{
    ProjectionCheckpoint _checkpoint;

    public List<ProjectionBatchContext> Contexts { get; } = [];

    public List<ulong> CommittedOffsets { get; } = [];

    public TestProjection Projection { get; } = new();

    public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(
        CheckpointIdentity identity,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_checkpoint);
    }

    public ValueTask<IProjectionBatch> BeginAsync(
        ProjectionBatchContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Contexts.Add(context);
        return ValueTask.FromResult<IProjectionBatch>(
            new RecordingProjectionBatch(Projection, CommittedOffsets, checkpoint => _checkpoint = checkpoint));
    }
}
