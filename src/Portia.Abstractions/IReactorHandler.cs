namespace Cntryl.Portia;

/// <summary>
/// Handles one event type for a reactor. A reactor implements this once per event type it
/// reacts to; the compiler enforces the handler's signature, so there is no naming convention
/// to get wrong or silently miss.
/// </summary>
/// <typeparam name="TEvent">The concrete event type handled.</typeparam>
public interface IReactorHandler<in TEvent>
    where TEvent : DomainEvent
{
    /// <summary>
    /// Handles the event.
    /// </summary>
    /// <param name="context">The reactor context.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing asynchronous event handling.</returns>
    ValueTask HandleAsync(IReactorContext<TEvent> context, CancellationToken ct);
}
