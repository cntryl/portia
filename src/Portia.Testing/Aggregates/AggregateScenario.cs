namespace Cntryl.Portia.Testing;

/// <summary>Provides business tests with replay and immutable snapshots of aggregate lifecycle collections.</summary>
/// <typeparam name="TAggregate">The aggregate under test.</typeparam>
/// <param name="aggregate">The aggregate under test.</param>
public sealed class AggregateScenario<TAggregate>(TAggregate aggregate)
    where TAggregate : Aggregate
{
    /// <summary>Gets the aggregate under test.</summary>
    public TAggregate Aggregate { get; } = aggregate ?? throw new ArgumentNullException(nameof(aggregate));

    /// <summary>Gets a snapshot of the pending raised events.</summary>
    public IReadOnlyList<DomainEvent> PendingEvents => [.. Aggregate.UncommittedEvents];

    /// <summary>Gets a snapshot of the pending audits.</summary>
    public IReadOnlyList<DomainEvent> PendingAudits => [.. Aggregate.UncommittedAudits];

    /// <summary>
    ///     Gets the number of state-changing events in committed history. The events themselves are
    ///     not retained: an aggregate replays its stream and is caught up in place, so keeping every
    ///     event it has seen would grow the instance without bound.
    /// </summary>
    public ulong CommittedEventCount => Aggregate.CommittedStreamPosition;

    /// <summary>Replays seeded state-changing history through the aggregate's normal validation.</summary>
    /// <param name="events">Already classified and identified state-changing events, in version order.</param>
    /// <returns>This scenario.</returns>
    public AggregateScenario<TAggregate> Given(params DomainEvent[] events)
    {
        Aggregate.Load(events);
        return this;
    }
}
