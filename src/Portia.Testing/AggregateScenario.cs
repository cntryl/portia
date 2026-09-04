namespace Cntryl.Portia;

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

    /// <summary>Gets a snapshot of committed state-changing history.</summary>
    public IReadOnlyList<DomainEvent> CommittedEvents => [.. Aggregate.CommittedEvents];

    /// <summary>Replays seeded state-changing history through the aggregate's normal validation.</summary>
    /// <param name="events">Already classified and identified state-changing events, in version order.</param>
    /// <returns>This scenario.</returns>
    public AggregateScenario<TAggregate> Given(params DomainEvent[] events)
    {
        Aggregate.Load(events);
        return this;
    }
}
