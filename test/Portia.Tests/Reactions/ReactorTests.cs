using System.Globalization;

namespace Cntryl.Portia;

/// <summary>
///     Verifies generated reactor dispatch.
/// </summary>
public sealed class ReactorTests
{
    /// <summary>
    ///     Verifies that a reactor's async handler is invoked and can raise a command against
    ///     another aggregate through the aggregate repository.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchAsyncHandlerAndRaiseCommandGivenRepository()
    {
        // Arrange
        var sourceId = Uuid.CreateVersion4();
        var repository = new RecordingAggregateRepository();
        var reactor = new TestReactor(repository);
        var stream = new EventStreamAddress("test", "reactors", sourceId.ToString());
        var ev = Committed(new ValueChanged(42), sourceId, 1);

        // Act
        await reactor.ReactAsync(new DomainEventRecord(stream, ev, 0, new EventCursor("1")), TestContext.Current.CancellationToken);

        // Assert
        var target = Assert.Single(repository.SavedAggregates);
        Assert.Equal(42, target.Value);
    }

    /// <summary>
    ///     Verifies that a reactor dispatches to the handler matching its event type.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchHandlerGivenMatchingEventType()
    {
        // Arrange
        var repository = new RecordingAggregateRepository();
        var reactor = new TestReactor(repository);
        var stream = new EventStreamAddress("test", "reactors", "one");
        var ev = Committed(new ValueIncremented(3), Uuid.CreateVersion4(), 1);

        // Act
        await reactor.ReactAsync(new DomainEventRecord(stream, ev, 0, new EventCursor("1")), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, reactor.LastIncrementAmount);
    }

    /// <summary>
    ///     Verifies that an unhandled event type is silently skipped, not an error — a reactor's
    ///     pattern is expected to span more than it handles, and filtering by event type (its
    ///     handler interfaces) is exactly how that's supposed to work.
    /// </summary>
    [Fact]
    public async Task ShouldSkipEventGivenUnhandledEventType()
    {
        // Arrange
        var repository = new RecordingAggregateRepository();
        var reactor = new TestReactor(repository);
        var stream = new EventStreamAddress("test", "reactors", "one");
        var ev = Committed(new ValueAudited("unhandled"), Uuid.CreateVersion4(), 1);

        // Act
        await reactor.ReactAsync(new DomainEventRecord(stream, ev, 0, new EventCursor("1")), TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(repository.SavedAggregates);
        Assert.Null(reactor.LastIncrementAmount);
    }

    /// <summary>Stable effect IDs vary only with checkpoint, source event, and effect name.</summary>
    [Fact]
    public void ShouldCreateStableDistinctEffectIdsGivenReactionContextWhenNamingEffects()
    {
        // Arrange
        var reactor = new TestReactor(new RecordingAggregateRepository(),
            pattern: EventStreamPattern.ForTenant("reactors"));
        var ev = Committed(new ValueChanged(42), Uuid.CreateVersion4(), 1);
        var record = new DomainEventRecord(new EventStreamAddress("test", "reactors", "one"), ev, 0,
            new EventCursor("1"));

        // Act
        var context = new ReactionExecutionContext(record, RequestActor.System);

        // Assert
        Assert.Equal(reactor.EffectId(context, "email"), reactor.EffectId(context, "email"));
        Assert.NotEqual(reactor.EffectId(context, "email"), reactor.EffectId(context, "audit"));
    }

    /// <summary>Structured effect identities have a deterministic derivation for bound and unbound workloads.</summary>
    [Theory]
    [InlineData(null, null, "737f5296-5c8e-59c0-9a32-262dd8d0bc5c")]
    [InlineData("orders", null, "8a529365-b054-5bc5-8749-b327f24b8e55")]
    [InlineData("orders", "acme", "0d9152c1-6c33-5704-8297-c74eb6f552ea")]
    public void ShouldCreateDeterministicEffectIdGivenReactionIdentity(
        string? workloadName, string? tenantValue, string expectedId)
    {
        // Arrange
        var reactor = new TestReactor(new RecordingAggregateRepository(),
            pattern: EventStreamPattern.ForTenant("reactors"));
        if (workloadName is not null)
        {
            TenantId? tenant = tenantValue is null ? null : new TenantId(tenantValue);
            reactor.BindWorkload(new WorkloadIdentity(workloadName, tenant), null);
        }

        var ev = new ValueChanged(42);
        ev.AttachMetadata(new DomainEventMetadata(
            Uuid.Parse("11111111-1111-4111-8111-111111111111", CultureInfo.InvariantCulture),
            Uuid.Parse("22222222-2222-4222-8222-222222222222", CultureInfo.InvariantCulture),
            1,
            new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero)));
        var record = new DomainEventRecord(
            new EventStreamAddress("acme", "reactors", "one"), ev, 0, new EventCursor("1"));
        var context = new ReactionExecutionContext(record, RequestActor.System);

        // Act
        var effectId = reactor.EffectId(context, "email");

        // Assert
        Assert.Equal(Uuid.Parse(expectedId, CultureInfo.InvariantCulture), effectId);
    }

    /// <summary>Binding is idempotent for one identity and rejects accidental instance reuse.</summary>
    [Fact]
    public void ShouldRejectDifferentIdentityGivenAlreadyBoundReactorWhenBindingAgain()
    {
        // Arrange
        var reactor = new TestReactor(new RecordingAggregateRepository(),
            pattern: EventStreamPattern.ForTenant("reactors"));
        var first = new WorkloadIdentity("test-reactor", new TenantId("one"));
        reactor.BindWorkload(first, null);

        // Act
        reactor.BindWorkload(first, null);

        // Assert
        _ = Assert.Throws<InvalidOperationException>(() =>
            reactor.BindWorkload(new WorkloadIdentity("test-reactor", new TenantId("two")), null));
    }

    static T Committed<T>(T ev, Uuid aggregateId, ulong aggregateVersion)
        where T : DomainEvent
    {
        ev.AttachMetadata(new DomainEventMetadata(
            Uuid.CreateVersion4(),
            aggregateId,
            aggregateVersion,
            DateTimeOffset.UtcNow));
        return ev;
    }
}
