namespace Cntryl.Portia;

/// <summary>
///     Reacts to bounded batches and saves progress after the complete batch succeeds.
///     External effects can be replayed after failure; batching does not make them transactional.
/// </summary>
public abstract class BatchReactor(
    IProjectionCheckpointStore checkpoints,
    EventStreamPattern pattern,
    string? name = null)
    : Reactor(checkpoints, pattern, name)
{
    internal sealed override bool IsBatch => true;
}
