namespace Cntryl.Portia;

/// <summary>
/// Verifies checkpointed reactor execution.
/// </summary>
public sealed class ReactorRunnerTests
{
    /// <summary>
    /// Verifies that every currently readable event is dispatched and the checkpoint advances
    /// past the last one.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchReadableEventsAndAdvanceCheckpointWhenRun()
    {
        var id = Uuid.CreateVersion7();
        var stream = new EventStreamAddress("test", "reactors", id.ToString());
        var store = new InMemoryEventStore();
        await store.AppendAsync(stream, 0, [
            Committed(new ValueChanged(40), id, 1),
            Committed(new ValueIncremented(1), id, 2),
            Committed(new ValueIncremented(1), id, 3),
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
    /// Verifies that running with nothing to read leaves the checkpoint unchanged.
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

    static T Committed<T>(T ev, Uuid aggregateId, ulong aggregateVersion)
        where T : DomainEvent
    {
        ev.AttachMetadata(new DomainEventMetadata(
            Uuid.CreateVersion7(),
            aggregateId,
            aggregateVersion,
            DateTimeOffset.UtcNow));
        return ev;
    }
}
