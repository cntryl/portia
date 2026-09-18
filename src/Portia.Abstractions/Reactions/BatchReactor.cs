namespace Cntryl.Portia;

/// <summary>
///     Reacts to bounded batches and saves progress after the complete batch succeeds.
///     External effects can be replayed after failure; batching does not make them transactional.
/// </summary>
/// <param name="checkpoints">The store that persists this reactor's progress.</param>
/// <param name="pattern">The event streams this reactor consumes.</param>
/// <param name="name">
///     The stable checkpoint name, or <see langword="null" /> to use the reactor's
///     own type name.
/// </param>
public abstract class BatchReactor(
    IProjectionCheckpointStore checkpoints,
    EventStreamPattern pattern,
    string? name = null)
    : Reactor(checkpoints, pattern, name)
{
    internal sealed override bool IsBatch => true;
}
