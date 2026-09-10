namespace Cntryl.Portia;

/// <summary>Handles an ordered contiguous group of events inside a bounded projection batch.</summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public interface IBatchProjectorHandler<in TEvent>
    where TEvent : DomainEvent
{
    /// <summary>Applies events in source order through constructor-injected dependencies.</summary>
    ValueTask HandleAsync(IReadOnlyList<TEvent> events, IProjectorContext context, CancellationToken ct);
}
