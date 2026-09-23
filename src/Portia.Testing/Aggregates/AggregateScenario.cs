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

    /// <summary>
    ///     Replays history through the aggregate's normal validation. Events without metadata are attached to this
    ///     aggregate and numbered from its current version; events that already carry metadata replay as seeded.
    /// </summary>
    /// <param name="events">State-changing events, in version order.</param>
    /// <returns>This scenario.</returns>
    public AggregateScenario<TAggregate> Given(params DomainEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        SaveCommitted();
        var version = Aggregate.Version;
        foreach (var ev in events)
        {
            ArgumentNullException.ThrowIfNull(ev);
            version++;
            if (ev.AttachedMetadata is null)
                _ = DomainEventSeed.Attach(ev, Aggregate.Id, version);
        }

        Aggregate.Load(events);
        return this;
    }

    /// <summary>
    ///     Runs an operation and applies its disposition as the executor would: a committed operation leaves what
    ///     it produced in <see cref="PendingEvents" /> and <see cref="PendingAudits" /> until the next
    ///     <see cref="Given" /> or <c>When</c> saves it; a discarded or throwing one leaves nothing.
    /// </summary>
    /// <param name="operation">The operation under test.</param>
    /// <returns>The result the caller would receive.</returns>
    public Result When(Func<TAggregate, AggregateOutcome> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        SaveCommitted();
        var outcome = Run(operation);
        Apply(outcome.Disposition);
        return outcome.Result;
    }

    /// <inheritdoc cref="When(Func{TAggregate, AggregateOutcome})" />
    /// <typeparam name="TOut">The type of the value on success.</typeparam>
    public Result<TOut> When<TOut>(Func<TAggregate, AggregateOutcome<TOut>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        SaveCommitted();
        var outcome = Run(operation);
        Apply(outcome.Disposition);
        return outcome.Result;
    }

    // The executor saves a committed operation as it returns; the scenario defers that save to the next Given or
    // When so the test can inspect what would have been written. Records raised by calling the aggregate directly
    // are saved the same way.
    void SaveCommitted()
    {
        if (Aggregate.UncommittedEvents.Count != 0 || Aggregate.UncommittedAudits.Count != 0)
            Aggregate.Save();
    }

    // An operation that throws commits nothing, as with the executor, and the instance refuses further use.
    TOutcome Run<TOutcome>(Func<TAggregate, TOutcome> operation)
    {
        try
        {
            return operation(Aggregate);
        }
        catch
        {
            Aggregate.DiscardPending();
            throw;
        }
    }

    void Apply(AggregateDisposition disposition)
    {
        if (disposition == AggregateDisposition.Commit)
            return;

        Aggregate.DiscardPending();
        if (disposition != AggregateDisposition.Discard)
        {
            throw new InvalidOperationException(
                $"An operation on aggregate '{typeof(TAggregate).FullName}' returned an uninitialized {nameof(AggregateOutcome)}; return AggregateOutcome.Commit or AggregateOutcome.Discard.");
        }
    }
}
