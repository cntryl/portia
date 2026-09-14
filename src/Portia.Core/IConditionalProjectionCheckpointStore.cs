namespace Cntryl.Portia;

/// <summary>Internal optimistic checkpoint contract for adapters that can compare and write atomically.</summary>
interface IConditionalProjectionCheckpointStore
{
    ValueTask SaveAsync(CheckpointIdentity identity, ProjectionCheckpoint expected,
        ProjectionCheckpoint checkpoint, CancellationToken ct = default);
}
