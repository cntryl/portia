namespace Cntryl.Portia;

/// <summary>
///     Projects bounded batches with one atomic data-and-checkpoint commit per batch.
///     Implement batch handler interfaces for bulk writes, or event handlers for ordered application.
/// </summary>
/// <param name="store">The repository that holds both the projected data and its checkpoint.</param>
/// <param name="pattern">The event streams this projector consumes.</param>
/// <param name="name">
///     The stable checkpoint name, or <see langword="null" /> to use the projector's
///     own type name.
/// </param>
public abstract class BatchProjector(IProjectionStore store, EventStreamPattern pattern, string? name = null)
    : Projector(store, pattern, name)
{
    internal sealed override bool IsBatch => true;
}
