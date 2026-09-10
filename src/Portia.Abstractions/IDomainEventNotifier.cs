namespace Cntryl.Portia;

/// <summary>Creates lightweight wake-up subscriptions for durable event patterns.</summary>
public interface IDomainEventNotifier
{
    /// <summary>Subscribes before a caller drains durable events, closing the read/subscribe race.</summary>
    ValueTask<IDomainEventSubscription> SubscribeAsync(
        EventStreamPattern pattern,
        CancellationToken ct = default);
}
