namespace Cntryl.Portia;

sealed class TransientReloadFailureCheckpointStore : IProjectionCheckpointStore
{
    readonly TaskCompletionSource _checkpointReloaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly InMemoryProjectionCheckpointStore _inner = new();
    int _loadAttempts;

    public Task CheckpointReloaded => _checkpointReloaded.Task;

    public async ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default)
    {
        var loadAttempt = Interlocked.Increment(ref _loadAttempts);

        if (loadAttempt == 2)
        {
            throw new InvalidOperationException("Simulated transient checkpoint reload failure.");
        }

        var checkpoint = await _inner.LoadAsync(identity, ct);

        if (loadAttempt == 3)
        {
            _ = _checkpointReloaded.TrySetResult();
        }

        return checkpoint;
    }

    public ValueTask SaveAsync(
        CheckpointIdentity identity,
        ProjectionCheckpoint checkpoint,
        CancellationToken ct = default) => _inner.SaveAsync(identity, checkpoint, ct);
}
