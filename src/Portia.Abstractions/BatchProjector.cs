namespace Cntryl.Portia;

/// <summary>
///     Projects bounded batches with one atomic data-and-checkpoint commit per batch.
///     Implement batch handler interfaces for bulk writes, or event handlers for ordered application.
/// </summary>
public abstract class BatchProjector(IProjectionStore store, EventStreamPattern pattern, string? name = null)
    : Projector(store, pattern, name)
{
    internal sealed override bool IsBatch => true;
}
