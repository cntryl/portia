namespace Cntryl.Portia;

/// <summary>Handles one selected event type using constructor-injected application dependencies.</summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public interface IProjectorHandler<in TEvent>
    where TEvent : DomainEvent
{
    /// <summary>Applies an event inside the repository's current unit of work.</summary>
    /// <param name="context">The source event and its shared projection-batch metadata.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The application outcome; only success permits the batch to commit.</returns>
    ValueTask<Result> HandleAsync(IProjectorContext<TEvent> context, CancellationToken ct);
}
