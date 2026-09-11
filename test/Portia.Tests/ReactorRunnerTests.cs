namespace Cntryl.Portia;

/// <summary>
///     Verifies checkpointed reactor execution.
/// </summary>
public sealed class ReactorRunnerTests
{
    /// <summary>
    ///     Verifies that every currently readable event is dispatched and the checkpoint advances
    ///     past the last one.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchReadableEventsAndAdvanceCheckpointWhenRun()
    {
        var id = Uuid.CreateVersion4();
        var stream = new EventStreamAddress("test", "reactors", id.ToString());
        var store = new InMemoryEventStore();
        await store.AppendAsync(stream, 0, [
            Committed(new ValueChanged(40), id, 1),
            Committed(new ValueIncremented(1), id, 2),
            Committed(new ValueIncremented(1), id, 3)
        ]);
        var repository = new RecordingAggregateRepository();
        var reactor = new TestReactor(repository);
        var runner = new ReactorRunner(store);

        var checkpoint = await runner.RunAsync(reactor, ProjectionCheckpoint.Start);

        var target = Assert.Single(repository.SavedAggregates);
        Assert.Equal(40, target.Value);
        Assert.Equal(1, reactor.LastIncrementAmount);
        Assert.Equal(3UL, checkpoint.NextOffset);
    }

    /// <summary>
    ///     Verifies that running with nothing to read leaves the checkpoint unchanged.
    /// </summary>
    [Fact]
    public async Task ShouldKeepCheckpointWhenNothingIsReadable()
    {
        var store = new InMemoryEventStore();
        var reactor = new TestReactor(new RecordingAggregateRepository());
        var runner = new ReactorRunner(store);
        var checkpoint = new ProjectionCheckpoint(3);

        var nextCheckpoint = await runner.RunAsync(reactor, checkpoint);

        Assert.Equal(checkpoint, nextCheckpoint);
    }

    /// <summary>The legacy integer overload preserves its public parameter name on validation.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ShouldNameLegacyBatchSizeParameterWhenRejected(int maxBatchSize)
    {
        var runner = new ReactorRunner(new InMemoryEventStore());
        var reactor = new TestReactor(new RecordingAggregateRepository());

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.RunAsync(reactor, ProjectionCheckpoint.Start, maxBatchSize).AsTask());

        Assert.Equal(nameof(maxBatchSize), exception.ParamName);
    }

    /// <summary>The options entry point bounds a pass and resumes from durable progress.</summary>
    [Fact]
    public async Task ShouldBoundPassAndResumeWithOptionsEntryPoint()
    {
        var id = Uuid.CreateVersion4();
        var stream = new EventStreamAddress("test", "reactors", id.ToString());
        var store = new InMemoryEventStore();
        await store.AppendAsync(stream, 0, [
            Committed(new ValueChanged(1), id, 1),
            Committed(new ValueChanged(2), id, 2),
            Committed(new ValueChanged(3), id, 3)
        ]);
        var reactor = new TestReactor(new RecordingAggregateRepository());
        var runner = new ReactorRunner(store);
        var options = new ProjectionRunOptions { MaxEventsPerPass = 2 };

        var first = await runner.RunPassAsync(reactor, ProjectionCheckpoint.Start, options);
        var second = await runner.RunPassAsync(reactor, first, options);

        Assert.Equal(2UL, first.NextOffset);
        Assert.Equal(3UL, second.NextOffset);
    }

    /// <summary>
    ///     Documents the behavior when a caller doesn't opt into checkpointed batching (no
    ///     <see cref="IProjectionCheckpointStore" /> passed to <c>ReactorRunner.RunAsync</c>):
    ///     a failure partway through a pass returns no checkpoint at all, and a subsequent retry
    ///     re-reacts to every event already successfully handled earlier in that same pass. See
    ///     <see cref="ShouldOnlyReprocessCurrentBatchWhenCheckpointStoreIsSuppliedAndMidPassFailureOccurs" />
    ///     for the bounded version of this same scenario.
    /// </summary>
    [Fact]
    public async Task ShouldReprocessAlreadyHandledEventsOnRetryAfterMidPassFailure()
    {
        var id = Uuid.CreateVersion4();
        var stream = new EventStreamAddress("test", "reactors", id.ToString());
        var store = new InMemoryEventStore();
        await store.AppendAsync(stream, 0, [
            Committed(new ValueChanged(1), id, 1),
            Committed(new ValueChanged(2), id, 2),
            Committed(new ValueChanged(3), id, 3)
        ]);
        var reactor = new FlakyOnSecondEventReactor();
        var runner = new ReactorRunner(store);

        // First pass fails on the second event — no checkpoint is returned at all.
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(reactor, ProjectionCheckpoint.Start).AsTask());
        Assert.Equal([1], reactor.HandledValues);

        // A caller has nothing but the original (unchanged) checkpoint to retry from — the only
        // option is starting the whole pass over.
        var checkpoint = await runner.RunAsync(reactor, ProjectionCheckpoint.Start);

        // Event 1 was handled a second time on retry, even though it succeeded the first time —
        // exactly the "no bound on redone work, side effects may not be idempotent" gap.
        Assert.Equal([1, 1, 2, 3], reactor.HandledValues);
        Assert.Equal(3UL, checkpoint.NextOffset);
    }

    /// <summary>
    ///     Verifies the fix: when a caller supplies an <see cref="IProjectionCheckpointStore" /> and
    ///     a bounded batch size, a failure partway through a pass only loses progress back to the
    ///     start of the *current* batch — not the whole pass. Four events, batch size 2, the fourth
    ///     event fails: the first batch (events 1-2) must already be durably checkpointed by the time
    ///     the failure happens, so a retry starting from that saved checkpoint only re-reacts to
    ///     events 3-4, never events 1-2 again.
    /// </summary>
    [Fact]
    public async Task ShouldOnlyReprocessCurrentBatchWhenCheckpointStoreIsSuppliedAndMidPassFailureOccurs()
    {
        var id = Uuid.CreateVersion4();
        var stream = new EventStreamAddress("test", "reactors", id.ToString());
        var store = new InMemoryEventStore();
        await store.AppendAsync(stream, 0, [
            Committed(new ValueChanged(1), id, 1),
            Committed(new ValueChanged(2), id, 2),
            Committed(new ValueChanged(3), id, 3),
            Committed(new ValueChanged(4), id, 4)
        ]);
        var runner = new ReactorRunner(store);
        var checkpointStore = new InMemoryProjectionCheckpointStore();
        var reactor = new FlakyOnThirdEventReactor(checkpointStore);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(reactor, ProjectionCheckpoint.Start, 2).AsTask());

        // The first batch (events 1-2) must already be durably saved — not just held in memory —
        // by the time the second batch's failure propagates.
        var savedAfterFailure = await checkpointStore.LoadAsync(new CheckpointIdentity(reactor.Name, reactor.Pattern));
        Assert.Equal(2UL, savedAfterFailure.NextOffset);
        Assert.Equal([1, 2], reactor.HandledValues);

        // Retrying from the saved checkpoint (not ProjectionCheckpoint.Start) only re-reacts to
        // events 3-4 — events 1-2 are never handled a second time.
        var resumeFrom = await checkpointStore.LoadAsync(new CheckpointIdentity(reactor.Name, reactor.Pattern));
        var finalCheckpoint = await runner.RunAsync(reactor, resumeFrom, 2);

        Assert.Equal([1, 2, 3, 4], reactor.HandledValues);
        Assert.Equal(4UL, finalCheckpoint.NextOffset);
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
