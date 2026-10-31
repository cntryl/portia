namespace Cntryl.Portia;

/// <summary>Handles a contiguous group of triggering events with independent execution contexts.</summary>
/// <typeparam name="TEvent">The selected event type.</typeparam>
public interface IBatchReactorHandler<in TEvent>
    where TEvent : DomainEvent
{
    /// <summary>Reacts in source order. Each context must be used for effects caused by its event.</summary>
    /// <param name="contexts">One context per triggering event, in source order.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The application outcome; only success permits checkpoint advancement.</returns>
    ValueTask<Result> HandleAsync(IReadOnlyList<IReactorContext<TEvent>> contexts, CancellationToken ct);
}
