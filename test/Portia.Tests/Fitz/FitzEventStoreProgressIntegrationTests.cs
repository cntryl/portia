namespace Cntryl.Portia;

/// <summary>Verifies source-progress observations against the real Fitz client and broker.</summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
[Trait("Category", "BrokerIntegration")]
public sealed class FitzEventStoreProgressIntegrationTests(FitzBrokerFixture broker)
{
    /// <summary>Resource, area, and realm progress follows committed cursors without modifying event history.</summary>
    [Fact]
    public async Task ShouldReportRealFitzProgressWithoutAdvancingOrChangingTheSource()
    {
        await using var client = await broker.CreateClientAsync();
        var realm = "progress-" + Uuid.CreateVersion4();
        var first = new EventStreamAddress(realm, "orders", "one");
        var second = new EventStreamAddress(realm, "orders", "two");
        var serializer = TestJson.DomainSerializer(
            new DomainEventTypeCatalog().Register<ValueChanged>(1, "test.value.changed"));
        var store = new FitzEventStore(client.Stream, serializer);
        var firstId = Uuid.CreateVersion4();
        await store.AppendAsync(first, 0,
        [
            DomainEventSeed.Attach(new ValueChanged(1), firstId, 1),
            DomainEventSeed.Attach(new ValueChanged(2), firstId, 2)
        ]);
        await store.AppendAsync(second, 0,
            [DomainEventSeed.Attach(new ValueChanged(3), Uuid.CreateVersion4(), 1)]);

        var patterns = new[]
        {
            EventStreamPattern.ForPattern(realm),
            EventStreamPattern.ForPattern(realm, "orders"),
            EventStreamPattern.ForPattern(realm, "orders", "one")
        };
        foreach (var pattern in patterns)
        {
            var records = await ReadAsync(store, pattern);
            var expectedCount = pattern.Resource is null ? 3UL : 2UL;
            Assert.Equal(expectedCount, (ulong)records.Count);

            var initial = await store.GetProgressAsync(pattern, EventCursor.Start);
            Assert.Equal(EventCursor.Start, initial.Position);
            Assert.Equal(expectedCount, initial.PendingEvents);
            Assert.Equal(expectedCount, initial.PendingEventsLowerBound);
            Assert.False(initial.IsCaughtUp);
            Assert.Equal(records[^1].NextCursor, initial.SourcePosition);

            var partial = await store.GetProgressAsync(pattern, records[0].NextCursor);
            Assert.Equal(records[0].NextCursor, partial.Position);
            Assert.Equal(expectedCount - 1, partial.PendingEvents);
            Assert.Equal(records[^1].NextCursor, partial.SourcePosition);
            Assert.False(partial.IsCaughtUp);

            var complete = await store.GetProgressAsync(pattern, records[^1].NextCursor);
            Assert.Equal(records[^1].NextCursor, complete.Position);
            Assert.Equal(0UL, complete.PendingEvents);
            Assert.Equal(0UL, complete.PendingEventsLowerBound);
            Assert.True(complete.IsCaughtUp);

            var unchanged = await ReadAsync(store, pattern);
            Assert.Equal(records.Select(record => record.Event.Metadata.EventId),
                unchanged.Select(record => record.Event.Metadata.EventId));
        }

        Assert.Equal(2UL, (await client.Stream.MetadataAsync(first.ToString())).RecordCount);
        Assert.Equal(1UL, (await client.Stream.MetadataAsync(second.ToString())).RecordCount);
    }

    static async Task<List<DomainEventRecord>> ReadAsync(FitzEventStore store, EventStreamPattern pattern)
    {
        var records = new List<DomainEventRecord>();
        await foreach (var record in store.ReadAsync(pattern, EventCursor.Start, default))
            records.Add(record);
        return records;
    }
}
