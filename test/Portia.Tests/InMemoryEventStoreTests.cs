using System.Globalization;

namespace Cntryl.Portia;

/// <summary>
///     Verifies the in-memory event-store contract.
/// </summary>
public sealed class InMemoryEventStoreTests
{
    /// <summary>
    ///     Verifies ordered append and incremental asynchronous reads.
    /// </summary>
    [Fact]
    public async Task ShouldReadEventsAfterKnownVersionWhenAppended()
    {
        var id = Uuid.CreateVersion4();
        var stream = new EventStreamAddress("test", "aggregates", id.ToString());
        var store = new InMemoryEventStore();
        var first = Committed(new ValueChanged(40), id, 1);
        var second = Committed(new ValueIncremented(2), id, 2);
        await store.AppendAsync(stream, 0, [first, second]);

        var events = new List<DomainEvent>();
        await foreach (var ev in store.ReadAsync(stream, 1))
            events.Add(ev.Event);

        Assert.Same(second, Assert.Single(events));
    }

    /// <summary>
    ///     Verifies optimistic concurrency rejects a stale writer without changing history.
    /// </summary>
    [Fact]
    public async Task ShouldRejectAppendWhenExpectedVersionIsStale()
    {
        var id = Uuid.CreateVersion4();
        var stream = new EventStreamAddress("test", "aggregates", id.ToString());
        var store = new InMemoryEventStore();
        var first = Committed(new ValueChanged(40), id, 1);
        await store.AppendAsync(stream, 0, [first]);

        var stale = Committed(new ValueIncremented(2), id, 1);
        _ = await Assert.ThrowsAsync<EventStreamConcurrencyException>(async () =>
            await store.AppendAsync(stream, 0, [stale]));

        var events = new List<DomainEvent>();
        await foreach (var ev in store.ReadAsync(stream))
            events.Add(ev.Event);

        Assert.Same(first, Assert.Single(events));
    }

    /// <summary>
    ///     Verifies projector reads preserve scope ordering and concrete source streams.
    /// </summary>
    [Fact]
    public async Task ShouldReadRealmPatternFromCheckpointAcrossStreams()
    {
        var firstId = Uuid.CreateVersion4();
        var secondId = Uuid.CreateVersion4();
        var firstStream = new EventStreamAddress("test", "orders", firstId.ToString());
        var secondStream = new EventStreamAddress("test", "customers", secondId.ToString());
        var store = new InMemoryEventStore();
        await store.AppendAsync(firstStream, 0, [Committed(new ValueChanged(40), firstId, 1)]);
        var second = Committed(new ValueChanged(42), secondId, 1);
        await store.AppendAsync(secondStream, 0, [second]);

        var records = new List<DomainEventRecord>();
        await foreach (var record in store.ReadAsync(EventStreamPattern.ForPattern("test"), new EventCursor("1"),
                           default))
            records.Add(record);

        var result = Assert.Single(records);
        Assert.Same(second, result.Event);
        Assert.Equal(secondStream, result.Stream);
        Assert.Equal(0UL, result.ResourceOffset);
        Assert.Equal(new EventCursor("2"), result.NextCursor);
    }

    /// <summary>Event IDs are stream-local and failed batches leave every scope contiguous.</summary>
    [Fact]
    public async Task ShouldAppendSharedEventIdsAtomicallyAcrossStreams()
    {
        var id = Uuid.CreateVersion4();
        var first = new EventStreamAddress("test", "orders", "first");
        var second = new EventStreamAddress("test", "orders", "second");
        var store = new InMemoryEventStore();
        var shared = Committed(new ValueChanged(1), id, 2);
        await store.AppendAsync(first, 0, [shared]);
        var other = Committed(new ValueChanged(2), id, 1);
        await store.AppendAsync(second, 0, [other, shared]);
        var rejected = Committed(new ValueChanged(3), id, 1);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AppendAsync(second, 2, [rejected, shared]));
        await store.AppendAsync(second, 2, [rejected]);
        await store.AppendAsync(first, 1, [Committed(new ValueChanged(4), id, 2)]);

        foreach (var pattern in new[]
                     { EventStreamPattern.ForPattern("test"), EventStreamPattern.ForPattern("test", "orders") })
        {
            var records = await store.ReadAsync(pattern, EventCursor.Start, default).ToListAsync();
            Assert.Equal(Enumerable.Range(1, 5).Select(value => value.ToString(CultureInfo.InvariantCulture)),
                records.Select(record => record.NextCursor.ToString()));
            Assert.Equal(new[] { first, second, second, second, first }, records.Select(record => record.Stream));
            Assert.Equal(new ulong[] { 0, 0, 1, 2, 1 }, records.Select(record => record.ResourceOffset));
            Assert.Equal(records.Skip(3), await store.ReadAsync(pattern, new EventCursor("3"), default).ToListAsync());
        }

        var resource = await store
            .ReadAsync(EventStreamPattern.ForPattern("test", "orders", "second"), EventCursor.Start, default)
            .ToListAsync();
        Assert.Equal(Enumerable.Range(1, 3).Select(value => value.ToString(CultureInfo.InvariantCulture)),
            resource.Select(record => record.NextCursor.ToString()));
        Assert.Equal(new DomainEvent[] { other, shared, rejected }, resource.Select(record => record.Event));
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
