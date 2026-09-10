using System.Globalization;

namespace Cntryl.Portia;

/// <summary>
/// Verifies generated reactor dispatch.
/// </summary>
public sealed class ReactorTests
{
    /// <summary>
    /// Verifies that a reactor's async handler is invoked and can raise a command against
    /// another aggregate through the aggregate repository.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchAsyncHandlerAndRaiseCommandThroughRepository()
    {
        var sourceId = Uuid.CreateVersion4();
        var repository = new RecordingAggregateRepository();
        var reactor = new TestReactor(repository);
        var stream = new EventStreamAddress("test", "reactors", sourceId.ToString());
        var ev = Committed(new ValueChanged(42), sourceId, 1);

        await reactor.ReactAsync(new DomainEventRecord(stream, ev, 0, 0, 0), default);

        var target = Assert.Single(repository.SavedAggregates);
        Assert.Equal(42, target.Value);
    }

    /// <summary>
    /// Verifies that a reactor dispatches to the handler matching its event type.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchMatchingHandler()
    {
        var repository = new RecordingAggregateRepository();
        var reactor = new TestReactor(repository);
        var stream = new EventStreamAddress("test", "reactors", "one");
        var ev = Committed(new ValueIncremented(3), Uuid.CreateVersion4(), 1);

        await reactor.ReactAsync(new DomainEventRecord(stream, ev, 0, 0, 0), default);

        Assert.Equal(3, reactor.LastIncrementAmount);
    }

    /// <summary>
    /// Verifies that an unhandled event type is silently skipped, not an error — a reactor's
    /// pattern is expected to span more than it handles, and filtering by event type (its
    /// handler interfaces) is exactly how that's supposed to work.
    /// </summary>
    [Fact]
    public async Task ShouldSkipUnhandledEventType()
    {
        var repository = new RecordingAggregateRepository();
        var reactor = new TestReactor(repository);
        var stream = new EventStreamAddress("test", "reactors", "one");
        var ev = Committed(new ValueAudited("unhandled"), Uuid.CreateVersion4(), 1);

        await reactor.ReactAsync(new DomainEventRecord(stream, ev, 0, 0, 0), default);

        Assert.Empty(repository.SavedAggregates);
        Assert.Null(reactor.LastIncrementAmount);
    }

    /// <summary>Stable effect IDs vary only with checkpoint, source event, and effect name.</summary>
    [Fact]
    public void ShouldCreateStableDistinctEffectIdsGivenReactionContextWhenNamingEffects()
    {
        var reactor = new TestReactor(new RecordingAggregateRepository());
        var ev = Committed(new ValueChanged(42), Uuid.CreateVersion4(), 1);
        var record = new DomainEventRecord(new EventStreamAddress("test", "reactors", "one"), ev, 0, 0, 0);
        var context = new ReactionExecutionContext(record, RequestActor.System);

        Assert.Equal(reactor.EffectId(context, "email"), reactor.EffectId(context, "email"));
        Assert.NotEqual(reactor.EffectId(context, "email"), reactor.EffectId(context, "audit"));
    }

    /// <summary>Previously persisted effect IDs remain valid after framework upgrades.</summary>
    [Theory]
    [InlineData(null, null, "abf6bc71-89ac-5e92-8c04-a3d15c49c658")]
    [InlineData("orders", null, "c2a326cf-7e40-5cd7-97a0-4a3e792ab8f6")]
    [InlineData("orders", "acme", "e44c94b5-3b26-5cd3-adf6-66b09d8b28a6")]
    public void ShouldPreservePersistedEffectIdGivenExistingReactionIdentity(
        string? workloadName, string? tenantValue, string expectedId)
    {
        var reactor = new TestReactor(new RecordingAggregateRepository());
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
            new EventStreamAddress("acme", "reactors", "one"), ev, 0, 0, 0);
        var context = new ReactionExecutionContext(record, RequestActor.System);

        var effectId = reactor.EffectId(context, "email");

        Assert.Equal(Uuid.Parse(expectedId, CultureInfo.InvariantCulture), effectId);
    }

    /// <summary>Binding is idempotent for one identity and rejects accidental instance reuse.</summary>
    [Fact]
    public void ShouldRejectDifferentIdentityGivenAlreadyBoundReactorWhenBindingAgain()
    {
        var reactor = new TestReactor(new RecordingAggregateRepository());
        var first = new WorkloadIdentity("test-reactor", new TenantId("one"));
        reactor.BindWorkload(first, null);
        reactor.BindWorkload(first, null);

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

sealed partial class TestReactor(IAggregateRepository repository, IProjectionCheckpointStore? checkpoints = null)
    : Reactor(checkpoints ?? new InMemoryProjectionCheckpointStore(), EventStreamPattern.ForPattern("test", "reactors"), "test-reactor"),
      IReactorHandler<ValueChanged>,
      IReactorHandler<ValueIncremented>
{
    public int? LastIncrementAmount { get; private set; }

    public Uuid EffectId(IReactorContext context, string name) => CreateEffectId(context, name);

    public async ValueTask HandleAsync(IReactorContext<ValueChanged> context, CancellationToken ct)
    {
        var target = await repository.HydrateAsync(new TestAggregate(Uuid.CreateVersion4()), ct);
        target.ChangeValue(context.Trigger.Value);
        await repository.SaveAsync(target, context, ct);
    }

    public ValueTask HandleAsync(IReactorContext<ValueIncremented> context, CancellationToken ct)
    {
        LastIncrementAmount = context.Trigger.Amount;
        return ValueTask.CompletedTask;
    }
}

sealed class RecordingAggregateRepository : IAggregateRepository
{
    public List<TestAggregate> SavedAggregates { get; } = [];

    public ValueTask<TAggregate> HydrateAsync<TAggregate>(TAggregate aggregate, CancellationToken ct = default)
        where TAggregate : Aggregate
        => ValueTask.FromResult(aggregate);

    public ValueTask SaveAsync<TAggregate>(TAggregate aggregate, IExecutionContext context, CancellationToken ct = default)
        where TAggregate : Aggregate
    {
        if (aggregate is TestAggregate testAggregate)
            SavedAggregates.Add(testAggregate);

        return ValueTask.CompletedTask;
    }
}
