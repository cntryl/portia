namespace Cntryl.Portia;

/// <summary>
/// Handles one event type for a projector. A projector implements this once per event type it
/// projects; the compiler enforces the handler's signature, so there is no naming convention to
/// get wrong or silently miss.
/// </summary>
/// <typeparam name="TEvent">The concrete event type handled.</typeparam>
/// <typeparam name="TProjection">The projection-specific application port.</typeparam>
public interface IProjectorHandler<in TEvent, TProjection>
{
    /// <summary>
    /// Handles the event.
    /// </summary>
    /// <param name="ev">The event to handle.</param>
    /// <param name="context">The projector context.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing asynchronous event handling.</returns>
    ValueTask HandleAsync(TEvent ev, IProjectorContext<TProjection> context, CancellationToken ct);
}
