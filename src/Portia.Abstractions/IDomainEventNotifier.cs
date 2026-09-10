namespace Cntryl.Portia;

/// <summary>Creates lightweight wake-up subscriptions for durable event patterns.</summary>
public interface IDomainEventNotifier
{
    /// <summary>Subscribes before a caller drains durable events, closing the read/subscribe race.</summary>
    /// <param name="pattern">The streams whose commits raise the signal.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A subscription that signals until it is disposed.</returns>
    ValueTask<IDomainEventSubscription> SubscribeAsync(
        EventStreamPattern pattern,
        CancellationToken ct = default);
}
