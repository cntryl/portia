namespace Cntryl.Portia;

/// <summary>
///     Verifies the aggregate event lifecycle.
/// </summary>
public sealed class AggregateTests
{
    /// <summary>
    ///     Verifies that an aggregate cannot have an empty identity.
    /// </summary>
    [Fact]
    public void ShouldRejectEmptyAggregateIdWhenConstructed()
    {
        var exception = Assert.Throws<ArgumentException>(() => new TestAggregate(Uuid.Empty));

        Assert.Equal("id", exception.ParamName);
    }

    /// <summary>
    ///     Verifies that raising an event updates state and tracks the event for persistence.
    /// </summary>
    [Fact]
    public void ShouldApplyAndTrackEventWhenRaised()
    {
        var aggregate = new TestAggregate(Uuid.CreateVersion4());

        aggregate.ChangeValue(42);

        var ev = Assert.Single(aggregate.UncommittedEvents);
        var valueChanged = Assert.IsType<ValueChanged>(ev);
        Assert.NotEqual(Uuid.Empty, valueChanged.Metadata.EventId);
        Assert.Equal(aggregate.Id, valueChanged.Metadata.AggregateId);
        Assert.Equal(1UL, valueChanged.Metadata.AggregateVersion);
        Assert.Equal(42, valueChanged.Value);
        Assert.Equal(42, aggregate.Value);
        Assert.Equal(1UL, aggregate.Version);
        Assert.Empty(aggregate.CommittedEvents);
    }

    /// <summary>
    ///     Verifies aggregate metadata creation is replaceable so applications can control time,
    ///     identity, correlation, and causation without modifying the aggregate base class.
    /// </summary>
    [Fact]
    public void ShouldUseConfiguredMetadataFactoryWhenEventIsRaised()
    {
        var aggregateId = Uuid.CreateVersion4();
        var eventId = Uuid.CreateVersion4();
        var correlationId = Uuid.CreateVersion4();
        var causationId = Uuid.CreateVersion4();
        var occurredOn = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var factory = new FixedDomainEventMetadataFactory(eventId, occurredOn, correlationId, causationId);
        var aggregate = new TestAggregate(aggregateId, factory);

        aggregate.ChangeValue(42);

        var metadata = Assert.Single(aggregate.UncommittedEvents).Metadata;
        Assert.Equal(eventId, metadata.EventId);
        Assert.Equal(aggregateId, metadata.AggregateId);
        Assert.Equal(1UL, metadata.AggregateVersion);
        Assert.Equal(occurredOn, metadata.OccurredOn);
        Assert.Equal(correlationId, metadata.CorrelationId);
        Assert.Equal(causationId, metadata.CausationId);
    }

    /// <summary>
    ///     Verifies a custom metadata factory cannot assign one event ID to two pending events in the
    ///     same append batch.
    /// </summary>
    [Fact]
    public void ShouldRejectFactoryEventIdAlreadyUsedByPendingEvent()
    {
        var eventId = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(Uuid.CreateVersion4(), new RepeatingDomainEventMetadataFactory(eventId));
        aggregate.ChangeValue(40);

        var exception = Assert.Throws<InvalidOperationException>(() => aggregate.ChangeValue(42));

        Assert.Contains(eventId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Equal(40, aggregate.Value);
        Assert.Equal(1UL, aggregate.Version);
        _ = Assert.Single(aggregate.UncommittedEvents);
    }

    /// <summary>
    ///     Verifies a custom metadata factory cannot reuse an event ID after the original event has
    ///     been committed.
    /// </summary>
    [Fact]
    public void ShouldRejectFactoryEventIdAlreadyUsedByCommittedEvent()
    {
        var eventId = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(Uuid.CreateVersion4(), new RepeatingDomainEventMetadataFactory(eventId));
        aggregate.ChangeValue(40);
        aggregate.Save();

        var exception = Assert.Throws<InvalidOperationException>(() => aggregate.ChangeValue(42));

        Assert.Contains(eventId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Equal(40, aggregate.Value);
        Assert.Equal(1UL, aggregate.Version);
        _ = Assert.Single(aggregate.CommittedEvents);
        Assert.Empty(aggregate.UncommittedEvents);
    }

    /// <summary>
    ///     Verifies that replay updates state, version, and committed history only.
    /// </summary>
    [Fact]
    public void ShouldTrackCommittedEventWhenReplayed()
    {
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);
        var ev = Committed(new ValueChanged(42), id, 1);

        aggregate.Load([ev]);

        Assert.Equal(42, aggregate.Value);
        Assert.Equal(1UL, aggregate.Version);
        Assert.Same(ev, Assert.Single(aggregate.CommittedEvents));
        Assert.Empty(aggregate.UncommittedEvents);
    }

    /// <summary>
    ///     Verifies that generated dispatch invokes the concrete overload for each event type.
    /// </summary>
    [Fact]
    public void ShouldDispatchEachEventToConcreteOnEventMethodWhenLoaded()
    {
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);

        aggregate.Load([
            Committed(new ValueChanged(40), id, 1),
            Committed(new ValueIncremented(2), id, 2)
        ]);

        Assert.Equal(42, aggregate.Value);
        Assert.Equal(2UL, aggregate.Version);
        Assert.Equal(2, aggregate.CommittedEvents.Count);
    }

    /// <summary>
    ///     Verifies that a cached aggregate can catch up from the next committed event.
    /// </summary>
    [Fact]
    public void ShouldLoadAdditionalCommittedEventsAfterCachedHistory()
    {
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);
        aggregate.Load([Committed(new ValueChanged(40), id, 1)]);

        aggregate.Load([
            Committed(new ValueIncremented(1), id, 2),
            Committed(new ValueIncremented(1), id, 3)
        ]);

        Assert.Equal(42, aggregate.Value);
        Assert.Equal(3UL, aggregate.Version);
        Assert.Equal(3, aggregate.CommittedEvents.Count);
        Assert.Empty(aggregate.UncommittedEvents);
    }

    /// <summary>
    ///     Verifies that a catch-up batch must continue directly after the cached version.
    /// </summary>
    [Fact]
    public void ShouldRejectAdditionalCommittedEventsWhenVersionIsNotContiguous()
    {
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);
        aggregate.Load([Committed(new ValueChanged(40), id, 1)]);

        _ = Assert.Throws<InvalidOperationException>(() => aggregate.Load([
            Committed(new ValueIncremented(2), id, 3)
        ]));

        Assert.Equal(40, aggregate.Value);
        Assert.Equal(1UL, aggregate.Version);
        _ = Assert.Single(aggregate.CommittedEvents);
    }

    /// <summary>
    ///     Verifies that duplicate event identities cannot appear in committed history.
    /// </summary>
    [Fact]
    public void ShouldRejectAdditionalCommittedEventWhenEventIdIsAlreadyLoaded()
    {
        var id = Uuid.CreateVersion4();
        var eventId = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);
        aggregate.Load([Committed(new ValueChanged(40), eventId, id, 1)]);

        _ = Assert.Throws<InvalidOperationException>(() => aggregate.Load([
            Committed(new ValueIncremented(2), eventId, id, 2)
        ]));

        Assert.Equal(40, aggregate.Value);
        Assert.Equal(1UL, aggregate.Version);
        _ = Assert.Single(aggregate.CommittedEvents);
    }

    /// <summary>
    ///     Verifies that catch-up cannot overwrite pending local decisions.
    /// </summary>
    [Fact]
    public void ShouldRejectCommittedEventsWhenAggregateHasUncommittedChanges()
    {
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);
        aggregate.ChangeValue(40);

        _ = Assert.Throws<InvalidOperationException>(() => aggregate.Load([
            Committed(new ValueIncremented(2), id, 2)
        ]));

        Assert.Equal(40, aggregate.Value);
        Assert.Equal(1UL, aggregate.Version);
        _ = Assert.Single(aggregate.UncommittedEvents);
        Assert.Empty(aggregate.CommittedEvents);
    }

    /// <summary>
    ///     Verifies that an audit is pending without changing aggregate state or version.
    /// </summary>
    [Fact]
    public void ShouldTrackAuditWithoutChangingStateWhenAudited()
    {
        var aggregate = new TestAggregate(Uuid.CreateVersion4());

        aggregate.Audit("value inspected");

        var audit = Assert.IsType<ValueAudited>(Assert.Single(aggregate.UncommittedAudits));
        Assert.Equal("value inspected", audit.Reason);
        Assert.Equal(aggregate.Id, audit.Metadata.AggregateId);
        Assert.Equal(0UL, audit.Metadata.AggregateVersion);
        Assert.Equal(0, aggregate.Value);
        Assert.Equal(0UL, aggregate.Version);
        Assert.Empty(aggregate.CommittedEvents);
        Assert.Empty(aggregate.UncommittedEvents);
    }

    /// <summary>
    ///     Verifies that committing promotes events and clears both pending buffers.
    /// </summary>
    [Fact]
    public void ShouldPromoteEventsAndClearPendingChangesWhenCommitted()
    {
        var aggregate = new TestAggregate(Uuid.CreateVersion4());
        aggregate.ChangeValue(42);
        var ev = Assert.Single(aggregate.UncommittedEvents);

        aggregate.Save();

        Assert.Same(ev, Assert.Single(aggregate.CommittedEvents));
        Assert.Empty(aggregate.UncommittedEvents);
        Assert.Empty(aggregate.UncommittedAudits);
        Assert.Equal(42, aggregate.Value);
        Assert.Equal(1UL, aggregate.Version);

        aggregate.Audit("value changed");
        aggregate.Audit("value inspected");
        Assert.Equal(2, aggregate.UncommittedAudits.Count);
        aggregate.Save();
        Assert.Same(ev, Assert.Single(aggregate.CommittedEvents));
        Assert.Empty(aggregate.UncommittedAudits);
        aggregate.ChangeValue(43);
        _ = Assert.Single(aggregate.UncommittedEvents);
    }

    /// <summary>
    ///     Verifies that events cannot be associated with a different aggregate instance.
    /// </summary>
    [Fact]
    public void ShouldRejectEventWhenAggregateIdDoesNotMatch()
    {
        var aggregate = new TestAggregate(Uuid.CreateVersion4());
        var ev = Committed(new ValueChanged(42), Uuid.CreateVersion4(), 1);

        _ = Assert.Throws<InvalidOperationException>(() => aggregate.Load([ev]));
        Assert.Equal(0, aggregate.Value);
        Assert.Equal(0UL, aggregate.Version);
        Assert.Empty(aggregate.CommittedEvents);
        Assert.Empty(aggregate.UncommittedEvents);
    }

    /// <summary>
    ///     Verifies that an event with no <see cref="Aggregate.On{TEvent}" /> registration at all —
    ///     not even a base type's — fails the same way an unhandled event always has.
    /// </summary>
    [Fact]
    public void ShouldThrowWhenApplyingEventWithNoRegisteredHandler()
    {
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);
        var ev = Committed(new UnhandledEvent(), id, 1);

        var exception = Assert.Throws<InvalidOperationException>(() => aggregate.Load([ev]));

        Assert.Contains("UnhandledEvent", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that registering two handlers for the same event type fails fast — at
    ///     construction, not silently overwriting the first registration or only surfacing once the
    ///     event is eventually applied.
    /// </summary>
    [Fact]
    public void ShouldThrowWhenRegisteringDuplicateHandlerForSameEventType() =>
        Assert.Throws<InvalidOperationException>(() => new DuplicateHandlerAggregate(Uuid.CreateVersion4()));

    static T Committed<T>(T ev, Uuid aggregateId, ulong aggregateVersion)
        where T : DomainEvent
        => Committed(ev, Uuid.CreateVersion4(), aggregateId, aggregateVersion);

    static T Committed<T>(T ev, Uuid eventId, Uuid aggregateId, ulong aggregateVersion)
        where T : DomainEvent
    {
        ev.AttachMetadata(new DomainEventMetadata(
            eventId,
            aggregateId,
            aggregateVersion,
            DateTimeOffset.UtcNow));
        return ev;
    }
}
