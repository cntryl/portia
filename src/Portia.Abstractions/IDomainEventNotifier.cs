namespace Cntryl.Portia;

/// <summary>Creates lightweight wake-up subscriptions for durable event patterns.</summary>
public interface IDomainEventNotifier
{
    /// <summary>Subscribes before a caller drains durable events, closing the read/subscribe race.</summary>
    ValueTask<IDomainEventSubscription> SubscribeAsync(
        EventStreamPattern pattern,
        CancellationToken ct = default);
}

/// <summary>A wake-up signal for commits matching one event-stream pattern.</summary>
public interface IDomainEventSubscription : IAsyncDisposable
{
    /// <summary>Waits until matching durable state may have changed.</summary>
    ValueTask WaitAsync(CancellationToken ct = default);
}
