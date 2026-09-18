namespace Cntryl.Portia;

/// <summary>Handles an ordered contiguous group of events inside a bounded projection batch.</summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public interface IBatchProjectorHandler<in TEvent>
    where TEvent : DomainEvent
{
    /// <summary>Applies events in source order through constructor-injected dependencies.</summary>
    /// <param name="events">The contiguous events to apply, in source order.</param>
    /// <param name="context">The projector context for this batch.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task that completes once every event has been applied.</returns>
    ValueTask HandleAsync(IReadOnlyList<TEvent> events, IProjectorContext context, CancellationToken ct);
}
