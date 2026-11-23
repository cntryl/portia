namespace Cntryl.Portia;

/// <summary>
///     Public optimistic checkpoint contract for reactor stores that can compare and write atomically.
/// </summary>
/// <remarks>
///     The expected cursor is opaque: stores compare it for equality and must reject a stale writer
///     with <see cref="ProjectionConcurrencyException"/>. Reactor hosting requires this capability;
///     an unconditional-only store is not safe for concurrent owners.
/// </remarks>
public interface IConditionalProjectionCheckpointStore
{
    /// <summary>Compares the expected checkpoint and advances it atomically.</summary>
    /// <param name="identity">The reactor component and canonical pattern.</param>
    /// <param name="expected">The checkpoint loaded before the pass began.</param>
    /// <param name="checkpoint">The checkpoint reached by the successful pass.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <exception cref="ProjectionConcurrencyException">Another owner advanced the checkpoint.</exception>
    ValueTask SaveAsync(
        CheckpointIdentity identity,
        ProjectionCheckpoint expected,
        ProjectionCheckpoint checkpoint,
        CancellationToken ct = default
    );
}
