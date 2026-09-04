using System.Collections.Concurrent;

namespace Cntryl.Portia;

/// <summary>
/// Keeps reactor checkpoints in memory — for tests, and for any single-instance
/// deployment that doesn't need a checkpoint to survive a restart. A real deployment that does
/// need that needs its own <see cref="IProjectionCheckpointStore" /> backed by durable storage.
/// </summary>
public sealed class InMemoryProjectionCheckpointStore : IProjectionCheckpointStore
{
    readonly ConcurrentDictionary<string, ProjectionCheckpoint> _checkpoints = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<ProjectionCheckpoint> LoadAsync(string name, CancellationToken ct = default) =>
        ValueTask.FromResult(_checkpoints.GetValueOrDefault(name, ProjectionCheckpoint.Start));

    /// <inheritdoc />
    public ValueTask SaveAsync(string name, ProjectionCheckpoint checkpoint, CancellationToken ct = default)
    {
        _checkpoints[name] = checkpoint;
        return ValueTask.CompletedTask;
    }
}
