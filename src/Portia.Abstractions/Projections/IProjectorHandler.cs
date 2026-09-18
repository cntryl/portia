namespace Cntryl.Portia;

/// <summary>Handles one selected event type using constructor-injected application dependencies.</summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public interface IProjectorHandler<in TEvent>
    where TEvent : DomainEvent
{
    /// <summary>Applies an event inside the repository's current unit of work.</summary>
    /// <param name="ev">The event to apply.</param>
    /// <param name="context">The projector context for the current batch.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task that completes once the event has been applied.</returns>
    ValueTask HandleAsync(TEvent ev, IProjectorContext context, CancellationToken ct);
}
