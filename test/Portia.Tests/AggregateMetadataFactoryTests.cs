using System.Collections.ObjectModel;

namespace Cntryl.Portia;

/// <summary>
///     Verifies that an aggregate does not trust the metadata factory an application supplies. The
///     factory is a public extension point — applications replace it to control time, identity, and
///     causation — so everything it returns about the aggregate's own identity and version is checked
///     before the event is tracked. A factory bug that slipped through would produce a stream that
///     replays as a different aggregate, or at a version optimistic concurrency then mis-compares.
/// </summary>
public sealed class AggregateMetadataFactoryTests
{
    /// <summary>
    ///     Verifies that a factory returning an empty event identity is refused, so an event can never
    ///     be stored with an identity that deduplication and replay cannot tell apart from any other.
    /// </summary>
    [Fact]
    public void ShouldRejectFactoryReturningEmptyEventId()
    {
        var aggregate = new TestAggregate(Uuid.CreateVersion4(), Factory(eventId: Uuid.Empty));

        var error = Assert.Throws<InvalidOperationException>(() => aggregate.ChangeValue(1));

        Assert.Contains("empty event ID", error.Message, StringComparison.Ordinal);
        Assert.Empty(aggregate.UncommittedEvents);
        Assert.Equal(0UL, aggregate.Version);
    }

    /// <summary>
    ///     Verifies that a factory cannot attribute an event to a different aggregate than the one that
    ///     raised it — that event would be written to this aggregate's stream while claiming to belong
    ///     to another, and every projector keyed on the aggregate identity would misfile it.
    /// </summary>
    [Fact]
    public void ShouldRejectFactoryReturningDifferentAggregateId()
    {
        var aggregate = new TestAggregate(Uuid.CreateVersion4(), Factory(aggregateId: Uuid.CreateVersion4()));

        var error = Assert.Throws<InvalidOperationException>(() => aggregate.ChangeValue(1));

        Assert.Contains("different aggregate ID", error.Message, StringComparison.Ordinal);
        Assert.Empty(aggregate.UncommittedEvents);
    }

    /// <summary>
    ///     Verifies that a factory cannot renumber the version the aggregate is about to move to.
    ///     Optimistic concurrency compares exactly that number, so a factory that returned its own
    ///     would let two writers both believe they had won.
    /// </summary>
    [Fact]
    public void ShouldRejectFactoryReturningDifferentAggregateVersion()
    {
        var aggregate = new TestAggregate(Uuid.CreateVersion4(), Factory(version: 99));

        var error = Assert.Throws<InvalidOperationException>(() => aggregate.ChangeValue(1));

        Assert.Contains("different aggregate version", error.Message, StringComparison.Ordinal);
        Assert.Empty(aggregate.UncommittedEvents);
    }

    /// <summary>
    ///     Verifies that a factory supplying a local time is refused at the point it is created, rather
    ///     than at save, so the stack trace names the factory rather than the store.
    /// </summary>
    [Fact]
    public void ShouldRejectFactoryReturningNonUtcOccurrenceTime()
    {
        var aggregate = new TestAggregate(Uuid.CreateVersion4(),
            Factory(occurredOn: new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.FromHours(-5))));

        var error = Assert.Throws<InvalidOperationException>(() => aggregate.ChangeValue(1));

        Assert.Contains("non-UTC occurrence time", error.Message, StringComparison.Ordinal);
        Assert.Empty(aggregate.UncommittedEvents);
    }

    /// <summary>
    ///     Verifies that the same validation guards an audit, which takes a different path through the
    ///     aggregate — audits do not advance the version, but they still may not misidentify it.
    /// </summary>
    [Fact]
    public void ShouldRejectFactoryReturningDifferentAggregateIdForAnAudit()
    {
        var aggregate = new TestAggregate(Uuid.CreateVersion4(), Factory(aggregateId: Uuid.CreateVersion4()));

        var error = Assert.Throws<InvalidOperationException>(() => aggregate.Audit("declined"));

        Assert.Contains("different aggregate ID", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that replay accepts only state-changing events. Audits are stored on their own
    ///     session streams precisely because they do not advance state, so replaying one as history
    ///     would leave the aggregate's version and its stream permanently disagreeing.
    /// </summary>
    [Fact]
    public void ShouldRejectAuditEventDuringReplay()
    {
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);
        var audit = new ValueAudited("declined");
        audit.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), id, 0, DateTimeOffset.UtcNow,
            IsAudit: true));

        var error = Assert.Throws<InvalidOperationException>(() => aggregate.Load([audit]));

        Assert.Contains("only state-changing events", error.Message, StringComparison.Ordinal);
        Assert.Equal(0UL, aggregate.Version);
    }

    /// <summary>
    ///     Verifies that catch-up works the same when history arrives as a plain read-only collection
    ///     rather than a list or array — a store is free to return any shape, and the fallback path has
    ///     its own validation and rollback code that must behave identically.
    /// </summary>
    [Fact]
    public void ShouldLoadCommittedEventsSuppliedAsAPlainReadOnlyCollection()
    {
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);

        aggregate.Load(ReadOnly(Committed(new ValueChanged(40), id, 1), Committed(new ValueIncremented(2), id, 2)));

        Assert.Equal(42, aggregate.Value);
        Assert.Equal(2UL, aggregate.Version);
        Assert.Equal(2UL, aggregate.CommittedStreamPosition);
    }

    /// <summary>
    ///     Verifies that the read-only collection path releases the identities it reserved when an event
    ///     in the batch fails to apply, so a retry with a corrected event of the same identity is still
    ///     accepted rather than rejected as a duplicate.
    /// </summary>
    [Fact]
    public void ShouldReleaseReservedIdentitiesWhenAReadOnlyCollectionBatchFailsToApply()
    {
        var id = Uuid.CreateVersion4();
        var eventId = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);

        _ = Assert.Throws<InvalidOperationException>(() =>
            aggregate.Load(ReadOnly(Committed(new UnhandledEvent(), eventId, id, 1))));
        aggregate.Load(ReadOnly(Committed(new ValueChanged(42), eventId, id, 1)));

        Assert.Equal(42, aggregate.Value);
        Assert.Equal(1UL, aggregate.Version);
    }

    /// <summary>
    ///     Verifies that the read-only collection path refuses history while local changes are pending,
    ///     the same as the list and array paths, so catch-up can never overwrite an undecided change.
    /// </summary>
    [Fact]
    public void ShouldRejectReadOnlyCollectionOfCommittedEventsWhileChangesArePending()
    {
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);
        aggregate.ChangeValue(1);

        var error = Assert.Throws<InvalidOperationException>(() =>
            aggregate.Load(ReadOnly(Committed(new ValueChanged(42), id, 1))));

        Assert.Contains("uncommitted changes", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that a non-contiguous batch is refused through the read-only collection path too,
    ///     and that nothing from it is applied.
    /// </summary>
    [Fact]
    public void ShouldRejectReadOnlyCollectionOfCommittedEventsWithAVersionGap()
    {
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);

        _ = Assert.Throws<InvalidOperationException>(() =>
            aggregate.Load(ReadOnly(Committed(new ValueChanged(40), id, 1), Committed(new ValueIncremented(2), id, 3))));

        Assert.Equal(0UL, aggregate.Version);
        Assert.Equal(0UL, aggregate.CommittedStreamPosition);
    }

    // Deliberately neither a list nor an array: that is the only way to reach the aggregate's
    // general IReadOnlyList path rather than its span fast paths.
    static ReadOnlyCollection<DomainEvent> ReadOnly(params DomainEvent[] events) => new(events);

    static T Committed<T>(T ev, Uuid aggregateId, ulong aggregateVersion) where T : DomainEvent =>
        Committed(ev, Uuid.CreateVersion4(), aggregateId, aggregateVersion);

    static T Committed<T>(T ev, Uuid eventId, Uuid aggregateId, ulong aggregateVersion) where T : DomainEvent
    {
        ev.AttachMetadata(new DomainEventMetadata(eventId, aggregateId, aggregateVersion, DateTimeOffset.UtcNow));
        return ev;
    }

    static MisbehavingMetadataFactory Factory(Uuid? eventId = null, Uuid? aggregateId = null, ulong? version = null,
        DateTimeOffset? occurredOn = null) => new(eventId, aggregateId, version, occurredOn);

    // Returns whatever it is told to, standing in for an application factory with a bug in it.
    sealed class MisbehavingMetadataFactory(Uuid? eventId, Uuid? aggregateId, ulong? version,
        DateTimeOffset? occurredOn) : IDomainEventMetadataFactory
    {
        public DomainEventMetadata Create(Uuid actualAggregateId, ulong actualVersion) => new(
            eventId ?? Uuid.CreateVersion4(),
            aggregateId ?? actualAggregateId,
            version ?? actualVersion,
            occurredOn ?? DateTimeOffset.UtcNow);
    }
}
