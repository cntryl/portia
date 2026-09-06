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
    : BaseReactor(checkpoints ?? new InMemoryProjectionCheckpointStore(), EventStreamPattern.ForPattern("test", "reactors"), "test-reactor"),
      IReactorHandler<ValueChanged>,
      IReactorHandler<ValueIncremented>
{
    public int? LastIncrementAmount { get; private set; }

    public async ValueTask HandleAsync(IReactorContext<ValueChanged> context, CancellationToken ct)
    {
        var target = await repository.HydrateAsync(new TestAggregate(Uuid.CreateVersion4()), ct);
        target.ChangeValue(context.Ev.Value);
        await repository.SaveAsync(target, context, ct);
    }

    public ValueTask HandleAsync(IReactorContext<ValueIncremented> context, CancellationToken ct)
    {
        LastIncrementAmount = context.Ev.Amount;
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
