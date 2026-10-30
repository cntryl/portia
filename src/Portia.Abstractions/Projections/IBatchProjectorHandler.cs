namespace Cntryl.Portia;

/// <summary>Handles an ordered contiguous group of events inside a bounded projection batch.</summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public interface IBatchProjectorHandler<in TEvent>
    where TEvent : DomainEvent
{
    /// <summary>Applies events in source order through constructor-injected dependencies.</summary>
    /// <param name="contexts">One context per contiguous source event, in order, sharing batch metadata.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The application outcome; only success permits the batch to commit.</returns>
    ValueTask<Result> HandleAsync(IReadOnlyList<IProjectorContext<TEvent>> contexts, CancellationToken ct);
}
