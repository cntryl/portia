using System.Collections.ObjectModel;

namespace Cntryl.Portia;

/// <summary>Replay application failures invalidate the instance without concealing the original failure.</summary>
public sealed class AggregateReplayFailureTests
{
    /// <summary>First, middle and final mutation failures discard array and read-only histories, including overrides.</summary>
    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(0, true, false)]
    [InlineData(1, true, false)]
    [InlineData(2, true, false)]
    [InlineData(0, false, true)]
    [InlineData(1, false, true)]
    [InlineData(2, false, true)]
    [InlineData(0, true, true)]
    [InlineData(1, true, true)]
    [InlineData(2, true, true)]
    public async Task ShouldDiscardAfterReplayApplicationFailure(int failureIndex, bool readOnly, bool useOverride)
    {
        var id = Uuid.CreateVersion4();
        var failure = new InvalidOperationException("Replay mutation failed.");
        var aggregate = new ReplayAggregate(id, failureIndex, useOverride, failure);
        var events = Enumerable.Range(0, 3).Select(index => (DomainEvent)
            DomainEventSeed.Attach(new ValueChanged(index + 1), id, (ulong)index + 1)).ToArray();
        IReadOnlyList<DomainEvent> history = readOnly ? new ReadOnlyCollection<DomainEvent>(events) : events;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => aggregate.Load(history)));
        Assert.Equal(failureIndex + 1, aggregate.Applied);
        Assert.Equal((ulong)failureIndex, aggregate.Version);
        Assert.Equal(0UL, aggregate.CommittedStreamPosition);
        Assert.Contains("discarded", Assert.Throws<InvalidOperationException>(() => aggregate.Load(history)).Message, StringComparison.Ordinal);
        Assert.Contains("discarded", Assert.Throws<InvalidOperationException>(aggregate.Raise).Message, StringComparison.Ordinal);
        Assert.Contains("discarded", Assert.Throws<InvalidOperationException>(aggregate.Audit).Message, StringComparison.Ordinal);
        var repository = new AggregateRepository(new InMemoryEventStore());
        var context = new RequestDispatchContext(RequestActor.System);
        var saveFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(aggregate, context).AsTask());
        Assert.Contains("discarded", saveFailure.Message, StringComparison.Ordinal);
        var hydrationFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.HydrateAsync(aggregate).AsTask());
        Assert.Contains("discarded", hydrationFailure.Message, StringComparison.Ordinal);
        var fresh = new ReplayAggregate(id, -1, useOverride, failure);
        fresh.Load(history);
        Assert.Equal(3, fresh.Applied);
        Assert.Equal(3UL, fresh.Version);
        Assert.Equal(3UL, fresh.CommittedStreamPosition);
    }

    /// <summary>Complete validation precedes application and releases reserved IDs for a corrected retry.</summary>
    [Fact]
    public void ShouldKeepPreApplyValidationFailureRetryable()
    {
        var id = Uuid.CreateVersion4();
        var aggregate = new ReplayAggregate(id, -1, false, new InvalidOperationException());
        var first = DomainEventSeed.Attach(new ValueChanged(1), id, 1);
        Assert.Throws<InvalidOperationException>(() => aggregate.Load([first, DomainEventSeed.Attach(new ValueChanged(2), id, 3)]));
        Assert.Equal(0, aggregate.Applied);
        aggregate.Load([first, DomainEventSeed.Attach(new ValueChanged(2), id, 2)]);
        Assert.Equal(2, aggregate.Applied);
    }

    sealed class ReplayAggregate : Aggregate
    {
        readonly int _failureIndex;
        readonly bool _useOverride;
        readonly Exception _failure;
        public ReplayAggregate(Uuid id, int failureIndex, bool useOverride, Exception failure)
            : base(id, new EventStreamAddress("test", "replay-failure", id.ToString()))
        {
            _failureIndex = failureIndex;
            _useOverride = useOverride;
            _failure = failure;
            On<ValueChanged>(_ => Mutate());
        }
        public int Applied { get; private set; }
        public void Raise() => RaiseEvent(new ValueChanged(10));
        public void Audit() => AuditEvent(new ValueAudited("test"));
        protected override void Apply(DomainEvent ev)
        {
            if (_useOverride)
                Mutate();
            else
                base.Apply(ev);
        }
        void Mutate()
        {
            if (Applied++ == _failureIndex)
                throw _failure;
        }
    }
}
