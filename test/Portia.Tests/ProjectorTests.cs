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

        await projector.ProjectAsync([new DomainEventRecord(stream, first, 0, 0, 0)],
            new CheckpointIdentity(projector.Name, projector.Pattern), default);
        await projector.ProjectAsync([new DomainEventRecord(stream, second, 1, 1, 1)],
            new CheckpointIdentity(projector.Name, projector.Pattern), default);

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
            new CheckpointIdentity(projector.Name, projector.Pattern), default);

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
}
