namespace Cntryl.Portia;

/// <summary>
/// Verifies the in-memory event-store contract.
/// </summary>
public sealed class InMemoryEventStoreTests
{
    /// <summary>
    /// Verifies ordered append and incremental asynchronous reads.
    /// </summary>
    [Fact]
    public async Task ShouldReadEventsAfterKnownVersionWhenAppended()
    {
        var id = Uuid.CreateVersion7();
        var stream = new EventStreamAddress("test", "aggregates", id.ToString());
        var store = new InMemoryEventStore();
        var first = Committed(new ValueChanged(40), id, 1);
        var second = Committed(new ValueIncremented(2), id, 2);
        await store.AppendAsync(stream, 0, [first, second]);

        var events = new List<DomainEvent>();
        await foreach (var ev in store.ReadAsync(stream, 1))
            events.Add(ev);

        Assert.Same(second, Assert.Single(events));
    }

    /// <summary>
    /// Verifies optimistic concurrency rejects a stale writer without changing history.
    /// </summary>
    [Fact]
    public async Task ShouldRejectAppendWhenExpectedVersionIsStale()
    {
        var id = Uuid.CreateVersion7();
        var stream = new EventStreamAddress("test", "aggregates", id.ToString());
        var store = new InMemoryEventStore();
        var first = Committed(new ValueChanged(40), id, 1);
        await store.AppendAsync(stream, 0, [first]);

        var stale = Committed(new ValueIncremented(2), id, 1);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.AppendAsync(stream, 0, [stale]));

        var events = new List<DomainEvent>();
        await foreach (var ev in store.ReadAsync(stream))
            events.Add(ev);

        Assert.Same(first, Assert.Single(events));
    }

    /// <summary>
    /// Verifies projector reads preserve scope ordering and concrete source streams.
    /// </summary>
    [Fact]
    public async Task ShouldReadRealmPatternFromCheckpointAcrossStreams()
    {
        var firstId = Uuid.CreateVersion7();
        var secondId = Uuid.CreateVersion7();
        var firstStream = new EventStreamAddress("test", "orders", firstId.ToString());
        var secondStream = new EventStreamAddress("test", "customers", secondId.ToString());
        var store = new InMemoryEventStore();
        await store.AppendAsync(firstStream, 0, [Committed(new ValueChanged(40), firstId, 1)]);
        var second = Committed(new ValueChanged(42), secondId, 1);
        await store.AppendAsync(secondStream, 0, [second]);

        var records = new List<DomainEventRecord>();
        await foreach (var record in store.ReadAsync(EventStreamPattern.ForPattern(realm: "test"), 1))
            records.Add(record);

        var result = Assert.Single(records);
        Assert.Same(second, result.Ev);
        Assert.Equal(secondStream, result.Stream);
        Assert.Equal(0UL, result.ResourceOffset);
        Assert.Equal(0UL, result.AreaOffset);
        Assert.Equal(1UL, result.RealmOffset);
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
