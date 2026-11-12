using System.Runtime.CompilerServices;

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
        // Arrange
        // Act
        var thrown = Record.Exception(() => new TestAggregate(Uuid.Empty));

        // Assert
        var exception = Assert.IsType<ArgumentException>(thrown);
        Assert.Equal("id", exception.ParamName);
    }

    /// <summary>
    ///     Verifies that raising an event updates state and tracks the event for persistence.
    /// </summary>
    [Fact]
    public void ShouldApplyAndTrackEventWhenRaised()
    {
        // Arrange
        var aggregate = new TestAggregate(Uuid.CreateVersion4());

        // Act
        aggregate.ChangeValue(42);

        // Assert
        var ev = Assert.Single(aggregate.UncommittedEvents);
        var valueChanged = Assert.IsType<ValueChanged>(ev);
        Assert.NotEqual(Uuid.Empty, valueChanged.Metadata.EventId);
        Assert.Equal(aggregate.Id, valueChanged.Metadata.AggregateId);
        Assert.Equal(1UL, valueChanged.Metadata.AggregateVersion);
        Assert.Equal(42, valueChanged.Value);
        Assert.Equal(42, aggregate.Value);
        Assert.Equal(1UL, aggregate.Version);
        Assert.Equal(0UL, aggregate.CommittedStreamPosition);
    }

    /// <summary>
    ///     Verifies aggregate metadata creation is replaceable so applications can control time,
    ///     identity, correlation, and causation without modifying the aggregate base class.
    /// </summary>
    [Fact]
    public void ShouldUseConfiguredMetadataFactoryWhenEventIsRaised()
    {
        // Arrange
        var aggregateId = Uuid.CreateVersion4();
        var eventId = Uuid.CreateVersion4();
        var correlationId = Uuid.CreateVersion4();
        var causationId = Uuid.CreateVersion4();
        var occurredOn = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var factory = new FixedDomainEventMetadataFactory(eventId, occurredOn, correlationId, causationId);
        var aggregate = new TestAggregate(aggregateId, factory);

        // Act
        aggregate.ChangeValue(42);

        // Assert
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
        // Arrange
        var eventId = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(Uuid.CreateVersion4(), new RepeatingDomainEventMetadataFactory(eventId));

        // Act
        aggregate.ChangeValue(40);

        // Assert
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
        // Arrange
        var eventId = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(Uuid.CreateVersion4(), new RepeatingDomainEventMetadataFactory(eventId));
        aggregate.ChangeValue(40);

        // Act
        aggregate.Save();

        // Assert
        var exception = Assert.Throws<InvalidOperationException>(() => aggregate.ChangeValue(42));

        Assert.Contains(eventId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Equal(40, aggregate.Value);
        Assert.Equal(1UL, aggregate.Version);
        Assert.Equal(1UL, aggregate.CommittedStreamPosition);
        Assert.Empty(aggregate.UncommittedEvents);
    }

    /// <summary>
    ///     Verifies that replay updates state, version, and committed history only.
    /// </summary>
    [Fact]
    public void ShouldTrackCommittedEventWhenReplayed()
    {
        // Arrange
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);
        var ev = Committed(new ValueChanged(42), id, 1);

        // Act
        aggregate.Load([ev]);

        // Assert
        Assert.Equal(42, aggregate.Value);
        Assert.Equal(1UL, aggregate.Version);
        Assert.Equal(1UL, aggregate.CommittedStreamPosition);
        Assert.Empty(aggregate.UncommittedEvents);
    }

    /// <summary>
    ///     Verifies that generated dispatch invokes the concrete overload for each event type.
    /// </summary>
    [Fact]
    public void ShouldDispatchEachEventToConcreteOnEventMethodWhenLoaded()
    {
        // Arrange
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);

        // Act
        aggregate.Load([
            Committed(new ValueChanged(40), id, 1),
            Committed(new ValueIncremented(2), id, 2)
        ]);

        // Assert
        Assert.Equal(42, aggregate.Value);
        Assert.Equal(2UL, aggregate.Version);
        Assert.Equal(2UL, aggregate.CommittedStreamPosition);
    }

    /// <summary>
    ///     Verifies that a cached aggregate can catch up from the next committed event.
    /// </summary>
    [Fact]
    public void ShouldLoadAdditionalCommittedEventsAfterCachedHistory()
    {
        // Arrange
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);
        aggregate.Load([Committed(new ValueChanged(40), id, 1)]);

        // Act
        aggregate.Load([
            Committed(new ValueIncremented(1), id, 2),
            Committed(new ValueIncremented(1), id, 3)
        ]);

        // Assert
        Assert.Equal(42, aggregate.Value);
        Assert.Equal(3UL, aggregate.Version);
        Assert.Equal(3UL, aggregate.CommittedStreamPosition);
        Assert.Empty(aggregate.UncommittedEvents);
    }

    /// <summary>
    ///     Verifies that a catch-up batch must continue directly after the cached version.
    /// </summary>
    [Fact]
    public void ShouldRejectAdditionalCommittedEventsWhenVersionIsNotContiguous()
    {
        // Arrange
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);

        // Act
        aggregate.Load([Committed(new ValueChanged(40), id, 1)]);

        // Assert
        _ = Assert.Throws<InvalidOperationException>(() => aggregate.Load([
            Committed(new ValueIncremented(2), id, 3)
        ]));

        Assert.Equal(40, aggregate.Value);
        Assert.Equal(1UL, aggregate.Version);
        Assert.Equal(1UL, aggregate.CommittedStreamPosition);
    }

    /// <summary>
    ///     Verifies that duplicate event identities cannot appear in committed history.
    /// </summary>
    [Fact]
    public void ShouldRejectAdditionalCommittedEventWhenEventIdIsAlreadyLoaded()
    {
        // Arrange
        var id = Uuid.CreateVersion4();
        var eventId = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);

        // Act
        aggregate.Load([Committed(new ValueChanged(40), eventId, id, 1)]);

        // Assert
        _ = Assert.Throws<InvalidOperationException>(() => aggregate.Load([
            Committed(new ValueIncremented(2), eventId, id, 2)
        ]));

        Assert.Equal(40, aggregate.Value);
        Assert.Equal(1UL, aggregate.Version);
        Assert.Equal(1UL, aggregate.CommittedStreamPosition);
    }

    /// <summary>
    ///     Verifies that catch-up cannot overwrite pending local decisions.
    /// </summary>
    [Fact]
    public void ShouldRejectCommittedEventsWhenAggregateHasUncommittedChanges()
    {
        // Arrange
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);

        // Act
        aggregate.ChangeValue(40);

        // Assert
        _ = Assert.Throws<InvalidOperationException>(() => aggregate.Load([
            Committed(new ValueIncremented(2), id, 2)
        ]));

        Assert.Equal(40, aggregate.Value);
        Assert.Equal(1UL, aggregate.Version);
        _ = Assert.Single(aggregate.UncommittedEvents);
        Assert.Equal(0UL, aggregate.CommittedStreamPosition);
    }

    /// <summary>
    ///     Verifies that an audit is pending without changing aggregate state or version.
    /// </summary>
    [Fact]
    public void ShouldTrackAuditWithoutChangingStateWhenAudited()
    {
        // Arrange
        var aggregate = new TestAggregate(Uuid.CreateVersion4());

        // Act
        aggregate.Audit("value inspected");

        // Assert
        var audit = Assert.IsType<ValueAudited>(Assert.Single(aggregate.UncommittedAudits));
        Assert.Equal("value inspected", audit.Reason);
        Assert.Equal(aggregate.Id, audit.Metadata.AggregateId);
        Assert.Equal(0UL, audit.Metadata.AggregateVersion);
        Assert.Equal(0, aggregate.Value);
        Assert.Equal(0UL, aggregate.Version);
        Assert.Equal(0UL, aggregate.CommittedStreamPosition);
        Assert.Empty(aggregate.UncommittedEvents);
    }

    /// <summary>
    ///     Verifies that committing promotes events and clears both pending buffers.
    /// </summary>
    [Fact]
    public void ShouldPromoteEventsAndClearPendingChangesWhenCommitted()
    {
        // Arrange
        var aggregate = new TestAggregate(Uuid.CreateVersion4());

        // Act
        aggregate.ChangeValue(42);

        // Assert
        var ev = Assert.Single(aggregate.UncommittedEvents);

        aggregate.Save();

        Assert.Equal(1UL, aggregate.CommittedStreamPosition);
        Assert.Empty(aggregate.UncommittedEvents);
        Assert.Empty(aggregate.UncommittedAudits);
        Assert.Equal(42, aggregate.Value);
        Assert.Equal(1UL, aggregate.Version);

        aggregate.Audit("value changed");
        aggregate.Audit("value inspected");
        Assert.Equal(2, aggregate.UncommittedAudits.Count);
        aggregate.Save();
        Assert.Equal(1UL, aggregate.CommittedStreamPosition);
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
        // Arrange
        var aggregate = new TestAggregate(Uuid.CreateVersion4());

        // Act
        var ev = Committed(new ValueChanged(42), Uuid.CreateVersion4(), 1);

        // Assert
        _ = Assert.Throws<InvalidOperationException>(() => aggregate.Load([ev]));
        Assert.Equal(0, aggregate.Value);
        Assert.Equal(0UL, aggregate.Version);
        Assert.Equal(0UL, aggregate.CommittedStreamPosition);
        Assert.Empty(aggregate.UncommittedEvents);
    }

    /// <summary>
    ///     Verifies that an event with no <see cref="Aggregate.On{TEvent}" /> registration at all —
    ///     not even a base type's — fails the same way an unhandled event always has.
    /// </summary>
    [Fact]
    public void ShouldThrowWhenApplyingEventWithNoRegisteredHandler()
    {
        // Arrange
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);

        // Act
        var ev = Committed(new UnhandledEvent(), id, 1);

        // Assert
        var exception = Assert.Throws<InvalidOperationException>(() => aggregate.Load([ev]));

        Assert.Contains("UnhandledEvent", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that validation does not permanently reserve an event ID when its handler fails
    ///     before the event is committed.
    /// </summary>
    [Fact]
    public void ShouldReleaseEventIdWhenApplyingCommittedEventFails()
    {
        // Arrange
        var id = Uuid.CreateVersion4();
        var eventId = Uuid.CreateVersion4();

        // Act
        var aggregate = new TestAggregate(id);

        // Assert
        _ = Assert.Throws<InvalidOperationException>(() => aggregate.Load([
            Committed(new UnhandledEvent(), eventId, id, 1)
        ]));

        var rejected = Assert.Throws<InvalidOperationException>(() => aggregate.ChangeValue(1));
        Assert.Contains("discarded", rejected.Message, StringComparison.Ordinal);
        aggregate = new TestAggregate(id);
        aggregate.Load([Committed(new ValueChanged(42), eventId, id, 1)]);

        Assert.Equal(42, aggregate.Value);
        Assert.Equal(1UL, aggregate.Version);
        Assert.Equal(1UL, aggregate.CommittedStreamPosition);
    }

    /// <summary>
    ///     Verifies that registering two handlers for the same event type fails fast — at
    ///     construction, not silently overwriting the first registration or only surfacing once the
    ///     event is eventually applied.
    /// </summary>
    [Fact]
    public void ShouldThrowWhenRegisteringDuplicateHandlerForSameEventType() =>
        Assert.Throws<InvalidOperationException>(() => new DuplicateHandlerAggregate(Uuid.CreateVersion4()));

    /// <summary>
    ///     Verifies that replayed history is applied and then released. An aggregate is expected to
    ///     rehydrate from its whole stream and may be kept across requests and caught up in place,
    ///     so holding every event it has ever seen would make the instance grow without bound for
    ///     the life of the process. Weak references state that exactly, rather than inferring it
    ///     from a memory measurement.
    /// </summary>
    [Fact]
    public void ShouldReleaseReplayedEventsOnceHistoryIsApplied()
    {
        // Arrange
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);

        var replayed = LoadWithoutRetaining(aggregate, id);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Act
        GC.Collect();

        // Assert
        Assert.All(replayed, reference => Assert.False(reference.IsAlive));
        Assert.Equal(3UL, aggregate.Version);
        Assert.Equal(3UL, aggregate.CommittedStreamPosition);
        Assert.Equal(44, aggregate.Value);
    }

    // The replayed events must have no strong reference left on the test's own stack, so they are
    // created, replayed and dropped inside a frame that has returned before the collection.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference[] LoadWithoutRetaining(TestAggregate aggregate, Uuid id)
    {
        DomainEvent[] history =
        [
            Committed(new ValueChanged(42), id, 1),
            Committed(new ValueIncremented(1), id, 2),
            Committed(new ValueIncremented(1), id, 3)
        ];
        aggregate.Load(history);
        return [.. history.Select(ev => new WeakReference(ev))];
    }

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
