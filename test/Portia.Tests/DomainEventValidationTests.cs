namespace Cntryl.Portia;

/// <summary>
///     Verifies the invariants every event store checks before it writes. These are the guarantees
///     hydration and every projector then assume without rechecking: that an event carries a real
///     identity, that attribution is complete rather than half-supplied, that times are UTC, and that a
///     batch is one aggregate's contiguous history rather than an arbitrary bag of events. A store that
///     writes past any of them corrupts a stream permanently, so each is checked in both directions.
/// </summary>
public sealed class DomainEventValidationTests
{
    static readonly DateTimeOffset Occurred = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    ///     Verifies the shapes that must be accepted: a plain state-changing event, an audit at version
    ///     zero, and a fully attributed event whose execution identity and actor arrive together.
    /// </summary>
    /// <param name="audit">Whether the event is an audit rather than a state change.</param>
    /// <param name="attributed">Whether the event carries execution and actor attribution.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ShouldAcceptWellFormedEvent(bool audit, bool attributed)
    {
        var metadata = Metadata(audit: audit) with
        {
            ExecutionId = attributed ? Uuid.CreateVersion4() : null,
            Actor = attributed ? new ActorAttribution("alice", "accounts") : null
        };

        DomainEventValidation.Validate(Event(metadata));
    }

    /// <summary>
    ///     Verifies that an event with no identity of its own, or none for its aggregate, is refused —
    ///     an empty identity would make the event indistinguishable from every other empty one, breaking
    ///     both deduplication and the aggregate a projector attributes it to.
    /// </summary>
    /// <param name="emptyEventId">Whether to blank the event identity rather than the aggregate's.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldRejectEmptyEventOrAggregateIdentity(bool emptyEventId)
    {
        var metadata = Metadata() with
        {
            EventId = emptyEventId ? Uuid.Empty : Uuid.CreateVersion4(),
            AggregateId = emptyEventId ? Uuid.CreateVersion4() : Uuid.Empty
        };

        var error = Assert.Throws<InvalidOperationException>(() => DomainEventValidation.Validate(Event(metadata)));

        Assert.Contains("identities cannot be empty", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that a supplied attribution identity is never the empty value. Absent attribution is
    ///     legitimate — an event saved without a request context has none — but an attribution that is
    ///     present and empty is a caller bug that would otherwise be stored as if it meant something.
    /// </summary>
    /// <param name="field">Which attribution identity to supply as empty.</param>
    [Theory]
    [InlineData("correlation")]
    [InlineData("causation")]
    [InlineData("execution")]
    public void ShouldRejectSuppliedButEmptyAttributionIdentity(string field)
    {
        var metadata = Metadata() with
        {
            CorrelationId = field == "correlation" ? Uuid.Empty : null,
            CausationId = field == "causation" ? Uuid.Empty : null,
            ExecutionId = field == "execution" ? Uuid.Empty : null,
            Actor = field == "execution" ? new ActorAttribution("alice", "accounts") : null
        };

        var error = Assert.Throws<InvalidOperationException>(() => DomainEventValidation.Validate(Event(metadata)));

        Assert.Contains("attribution identities cannot be empty", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that an execution identity and an actor are stored together or not at all: an
    ///     execution with no actor cannot answer "who", and an actor with no execution cannot be tied
    ///     back to the dispatch that produced the event.
    /// </summary>
    /// <param name="withExecution">Whether the execution identity is the half that was supplied.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldRejectExecutionIdentityAndActorSuppliedApart(bool withExecution)
    {
        var metadata = Metadata() with
        {
            ExecutionId = withExecution ? Uuid.CreateVersion4() : null,
            Actor = withExecution ? null : new ActorAttribution("alice", "accounts")
        };

        var error = Assert.Throws<InvalidOperationException>(() => DomainEventValidation.Validate(Event(metadata)));

        Assert.Contains("must be supplied together", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that actor attribution names both a subject and the authority that issued it — a
    ///     subject alone is not an identity anyone can resolve later, and a blank one is worse than none.
    /// </summary>
    /// <param name="subject">The subject to attribute the event to.</param>
    /// <param name="issuer">The issuing authority to attribute the event to.</param>
    [Theory]
    [InlineData("", "accounts")]
    [InlineData("   ", "accounts")]
    [InlineData("alice", "")]
    [InlineData("alice", "   ")]
    public void ShouldRejectActorAttributionMissingSubjectOrIssuer(string subject, string issuer)
    {
        var metadata = Metadata() with
        {
            ExecutionId = Uuid.CreateVersion4(),
            Actor = new ActorAttribution(subject, issuer)
        };

        var error = Assert.Throws<InvalidOperationException>(() => DomainEventValidation.Validate(Event(metadata)));

        Assert.Contains("subject and issuer", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that an occurrence time carrying a non-zero offset is refused rather than silently
    ///     stored, so a stream's times are comparable across the machines that wrote them.
    /// </summary>
    [Fact]
    public void ShouldRejectNonUtcOccurrenceTime()
    {
        var metadata = Metadata() with { OccurredOn = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.FromHours(2)) };

        var error = Assert.Throws<InvalidOperationException>(() => DomainEventValidation.Validate(Event(metadata)));

        Assert.Contains("must be UTC", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that only an audit may sit at aggregate version zero. A state-changing event that
    ///     does not advance the version would make two different states share one version, which is
    ///     exactly what optimistic concurrency relies on never happening.
    /// </summary>
    [Fact]
    public void ShouldRejectStateChangingEventAtVersionZero()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            DomainEventValidation.Validate(Event(Metadata(version: 0))));

        Assert.Contains("advance the aggregate version", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies the batch shapes a store must accept: contiguous raised versions for one aggregate,
    ///     and any number of audits pinned to the same state version.
    /// </summary>
    /// <param name="audit">Whether the batch is audits rather than raised events.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldAcceptBatchOfOneAggregatesEventsInOrder(bool audit)
    {
        var aggregateId = Uuid.CreateVersion4();
        DomainEventValidation.ValidateBatch([
            Event(Metadata(aggregateId, audit ? 3UL : 1UL, audit)),
            Event(Metadata(aggregateId, audit ? 3UL : 2UL, audit)),
            Event(Metadata(aggregateId, audit ? 3UL : 3UL, audit))
        ]);
    }

    /// <summary>
    ///     Verifies that a batch mixing aggregates, mixing audits with state changes, or skipping a
    ///     version is refused as a whole — a store that wrote any of these would leave a stream whose
    ///     replay no longer reconstructs the aggregate it claims to describe.
    /// </summary>
    /// <param name="defect">Which property of the second event breaks the batch.</param>
    [Theory]
    [InlineData("aggregate")]
    [InlineData("kind")]
    [InlineData("version")]
    public void ShouldRejectBatchThatIsNotOneAggregatesContiguousHistory(string defect)
    {
        var aggregateId = Uuid.CreateVersion4();
        var second = defect switch
        {
            "aggregate" => Metadata(Uuid.CreateVersion4(), 2),
            "kind" => Metadata(aggregateId, 2, true),
            _ => Metadata(aggregateId, 3)
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            DomainEventValidation.ValidateBatch([Event(Metadata(aggregateId, 1)), Event(second)]));

        Assert.Contains("one aggregate's raised events in order", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that one event identity cannot appear twice in a batch. Deduplication downstream is
    ///     keyed on that identity, so a duplicate inside a single append would be silently collapsed by
    ///     every consumer that reads the stream.
    /// </summary>
    [Fact]
    public void ShouldRejectBatchRepeatingOneEventIdentity()
    {
        var aggregateId = Uuid.CreateVersion4();
        var eventId = Uuid.CreateVersion4();

        var error = Assert.Throws<InvalidOperationException>(() => DomainEventValidation.ValidateBatch([
            Event(Metadata(aggregateId, 1) with { EventId = eventId }),
            Event(Metadata(aggregateId, 2) with { EventId = eventId })
        ]));

        Assert.Contains(eventId.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Contains("more than once", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies that a missing event is rejected before anything reads its metadata.</summary>
    [Fact]
    public void ShouldRejectMissingEvent() =>
        Assert.Throws<ArgumentNullException>(() => DomainEventValidation.Validate(null!));

    /// <summary>
    ///     Verifies that these same invariants hold through the public store boundary, not just in
    ///     isolation — an application never calls the validator itself, it calls append.
    /// </summary>
    [Fact]
    public async Task ShouldRejectInvalidBatchThroughTheStoreWithoutWritingAnything()
    {
        var store = new InMemoryEventStore();
        var stream = new EventStreamAddress("validation", "events", Uuid.CreateVersion4().ToString());
        var aggregateId = Uuid.CreateVersion4();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AppendAsync(stream, 0,
                [Event(Metadata(aggregateId, 1)), Event(Metadata(Uuid.CreateVersion4(), 2))]));

        await foreach (var record in store.ReadAsync(stream))
            Assert.Fail($"A rejected batch appended event {record.Event.Metadata.EventId}.");
    }

    static DomainEventMetadata Metadata(Uuid? aggregateId = null, ulong version = 1, bool audit = false) =>
        new(Uuid.CreateVersion4(), aggregateId ?? Uuid.CreateVersion4(), audit ? 0 : version, Occurred,
            IsAudit: audit);

    static DomainEventMetadata Metadata(bool audit) => Metadata(null, 1, audit);

    static ValidationEvent Event(DomainEventMetadata metadata)
    {
        var ev = new ValidationEvent();
        ev.AttachMetadata(metadata);
        return ev;
    }

    /// <summary>An event whose only purpose is to carry the metadata under test.</summary>
    [Discriminator("portia.tests.validation-event")]
    public sealed record ValidationEvent : DomainEvent;
}
