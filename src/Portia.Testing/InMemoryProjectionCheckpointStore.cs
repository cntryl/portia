using System.Collections.Concurrent;

namespace Cntryl.Portia.Testing;

/// <summary>
/// Keeps reactor checkpoints in memory — for tests, and for any single-instance
/// deployment that doesn't need a checkpoint to survive a restart. A real deployment that does
/// need that needs its own <see cref="IProjectionCheckpointStore" /> backed by durable storage.
/// </summary>
public sealed class InMemoryProjectionCheckpointStore : IProjectionCheckpointStore
{
    readonly ConcurrentDictionary<CheckpointIdentity, ProjectionCheckpoint> _checkpoints = new();

    /// <inheritdoc />
    public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default) =>
        ValueTask.FromResult(_checkpoints.GetValueOrDefault(identity, ProjectionCheckpoint.Start));

    /// <inheritdoc />
    public ValueTask SaveAsync(CheckpointIdentity identity, ProjectionCheckpoint checkpoint, CancellationToken ct = default)
    {
        _checkpoints[identity] = checkpoint;
        return ValueTask.CompletedTask;
    }
}
