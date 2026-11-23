using System.Collections.Concurrent;

namespace Cntryl.Portia.Testing;

/// <summary>
///     Keeps reactor checkpoints in memory — for tests, and for any single-instance
///     deployment that doesn't need a checkpoint to survive a restart. A real deployment that does
///     need that needs its own <see cref="IProjectionCheckpointStore" /> backed by durable storage.
/// </summary>
public sealed class InMemoryProjectionCheckpointStore : IProjectionCheckpointStore, IConditionalProjectionCheckpointStore
{
    readonly ConcurrentDictionary<CheckpointIdentity, ProjectionCheckpoint> _checkpoints = new();
    readonly object _gate = new();

    /// <inheritdoc />
    public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default) =>
        ValueTask.FromResult(_checkpoints.GetValueOrDefault(identity, ProjectionCheckpoint.Start));

    /// <inheritdoc />
    public ValueTask SaveAsync(
        CheckpointIdentity identity,
        ProjectionCheckpoint checkpoint,
        CancellationToken ct = default
    )
    {
        _checkpoints[identity] = checkpoint;
        return ValueTask.CompletedTask;
    }

    ValueTask IConditionalProjectionCheckpointStore.SaveAsync(
        CheckpointIdentity identity,
        ProjectionCheckpoint expected,
        ProjectionCheckpoint checkpoint,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(identity);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var actual = _checkpoints.GetValueOrDefault(identity, ProjectionCheckpoint.Start);

            if (actual != expected)
                throw new ProjectionConcurrencyException(
                    $"Checkpoint for '{identity.ComponentName}' changed from the expected value before save.");

            _checkpoints[identity] = checkpoint;
        }
        return ValueTask.CompletedTask;
    }
}
