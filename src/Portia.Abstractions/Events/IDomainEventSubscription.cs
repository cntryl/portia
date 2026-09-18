namespace Cntryl.Portia;

/// <summary>A wake-up signal for commits matching one event-stream pattern.</summary>
public interface IDomainEventSubscription : IAsyncDisposable
{
    /// <summary>Waits until matching durable state may have changed.</summary>
    /// <param name="ct">A token that can cancel the wait.</param>
    /// <returns>A task that completes when a matching commit may have occurred.</returns>
    ValueTask WaitAsync(CancellationToken ct = default);
}
