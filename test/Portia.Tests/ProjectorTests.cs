using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

/// <summary>
///     Verifies generated projector dispatch.
/// </summary>
public sealed class ProjectorTests
{
    /// <summary>
    ///     Verifies that multiple async handlers share one batch-scoped projection.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchHandlersThroughSameProjection()
    {
        var projector = new TestProjector(new RecordingProjectionTarget());
        var projection = ((ITestProjectionRepository)projector.Store).Projection;
        var stream = new EventStreamAddress("test", "projectors", "one");
        var first = Committed(new ValueChanged(40), 1);
        var second = Committed(new ValueIncremented(2), 2);
        var context = new ProjectorContext(new CheckpointIdentity(projector.Name, projector.Pattern));

        await projector.ProjectAsync([new DomainEventRecord(stream, first, 0, 0, 0)], context, default);
        await projector.ProjectAsync([new DomainEventRecord(stream, second, 1, 1, 1)], context, default);

        Assert.Equal(42, projection.Value);
        Assert.Equal(2, projection.HandlerCount);
        Assert.Equal(1, projection.LoadCount);
    }

    /// <summary>
    ///     Verifies trickle and rebuild execution use the same bounded batch pipeline.
    /// </summary>
    [Fact]
    public async Task ShouldCommitBoundedBatchesAndAdvanceCheckpointWhenRun()
    {
        var id = Uuid.CreateVersion4();
        var stream = new EventStreamAddress("test", "projectors", id.ToString());
        var store = new InMemoryEventStore();
        await store.AppendAsync(stream, 0, [
            Committed(new ValueChanged(40), id, 1),
            Committed(new ValueIncremented(1), id, 2),
            Committed(new ValueIncremented(1), id, 3)
        ]);
        var target = new RecordingProjectionTarget();
        var projector = new TestProjector(target);
        var runner = new ProjectorRunner(store);

        var checkpoint = await runner.RunAsync(
            projector,
            ProjectionCheckpoint.Start,
            new ProjectionRunOptions { MaxBatchSize = 2, RebuildId = "rebuild-1" });

        Assert.Equal(42, target.Projection.Value);
        Assert.Equal(3UL, checkpoint.NextOffset);
        Assert.Equal([2UL, 3UL], target.CommittedOffsets);
        Assert.All(target.Contexts, context => Assert.True(context.IsRebuild));
    }

    /// <summary>A pass stops at its record budget and resumes from the returned checkpoint.</summary>
    [Fact]
    public async Task ShouldBoundPassWithoutReadingAheadAndResume()
    {
        var id = Uuid.CreateVersion4();
        var stream = new EventStreamAddress("test", "projectors", id.ToString());
        var store = new InMemoryEventStore();
        await store.AppendAsync(stream, 0, [
            Committed(new ValueChanged(40), id, 1),
            Committed(new ValueIncremented(1), id, 2),
            Committed(new ValueIncremented(1), id, 3)
        ]);
        var target = new RecordingProjectionTarget();
        var projector = new TestProjector(target);
        var runner = new ProjectorRunner(store);
        var options = new ProjectionRunOptions { MaxBatchSize = 8, MaxEventsPerPass = 2 };

        var first = await runner.RunAsync(projector, ProjectionCheckpoint.Start, options);
        var second = await runner.RunAsync(projector, first, options);

        Assert.Equal(2UL, first.NextOffset);
        Assert.Equal(3UL, second.NextOffset);
        Assert.Equal([2UL, 3UL], target.CommittedOffsets);
    }

    /// <summary>A full pass that cannot advance its checkpoint must not request a hot retry.</summary>
    [Fact]
    public async Task ShouldNotContinueImmediatelyWhenBudgetExhaustionMakesNoProgress()
    {
        var stream = new EventStreamAddress("test", "projectors", "stale");
        var record = new DomainEventRecord(stream, Committed(new ValueChanged(40), 1), 0, 0, 0);
        var runner = new ProjectorRunner(new StaleOffsetEventReader(record));
        var projector = new TestProjector(new RecordingProjectionTarget());

        var result = await runner.RunPassAsync(projector, new ProjectionCheckpoint(1),
            new ProjectionRunOptions { MaxEventsPerPass = 1 });

        Assert.False(result.ContinueImmediately);
        Assert.Equal(1UL, result.Checkpoint.NextOffset);
    }

    /// <summary>Non-positive pass budgets are rejected before enumeration.</summary>
    [Fact]
    public async Task ShouldRejectNonPositivePassBudget()
    {
        var runner = new ProjectorRunner(new InMemoryEventStore());
        var projector = new TestProjector(new RecordingProjectionTarget());
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunAsync(projector,
            ProjectionCheckpoint.Start, new ProjectionRunOptions { MaxEventsPerPass = 0 }).AsTask());
    }

    /// <summary>
    ///     Verifies that an unhandled event type is silently skipped, not an error — a projector's
    ///     pattern is expected to span more than it handles, and filtering by event type (its
    ///     handler interfaces) is exactly how that's supposed to work.
    /// </summary>
    [Fact]
    public async Task ShouldSkipUnhandledEventType()
    {
        var projector = new TestProjector(new RecordingProjectionTarget());
        var projection = ((ITestProjectionRepository)projector.Store).Projection;
        var stream = new EventStreamAddress("test", "projectors", "one");
        var ev = Committed(new ValueAudited("unhandled"), 1);

        await projector.ProjectAsync([new DomainEventRecord(stream, ev, 0, 0, 0)],
            new ProjectorContext(new CheckpointIdentity(projector.Name, projector.Pattern)), default);

        Assert.Equal(0, projection.Value);
        Assert.Equal(0, projection.HandlerCount);
    }

    static T Committed<T>(T ev, ulong aggregateVersion)
        where T : DomainEvent
        => Committed(ev, Uuid.CreateVersion4(), aggregateVersion);

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

    sealed class StaleOffsetEventReader(DomainEventRecord record) : IDomainEventReader
    {
        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset = 0,
            CancellationToken ct = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, ulong fromOffset = 0,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return record;
        }
    }
}
