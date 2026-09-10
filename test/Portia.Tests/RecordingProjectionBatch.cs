namespace Cntryl.Portia;

sealed class RecordingProjectionBatch(
    TestProjection projection,
    List<ulong> committedOffsets,
    Action<ProjectionCheckpoint> saveCheckpoint) : IProjectionBatch
{
    public TestProjection Projection { get; } = projection;

    public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        committedOffsets.Add(checkpoint.NextOffset);
        saveCheckpoint(checkpoint);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
