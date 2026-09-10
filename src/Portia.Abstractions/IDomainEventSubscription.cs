namespace Cntryl.Portia;

/// <summary>A wake-up signal for commits matching one event-stream pattern.</summary>
public interface IDomainEventSubscription : IAsyncDisposable
{
    /// <summary>Waits until matching durable state may have changed.</summary>
    ValueTask WaitAsync(CancellationToken ct = default);
}
