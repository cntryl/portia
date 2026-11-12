namespace Cntryl.Portia;

/// <summary>Verifies that progress observations use the same scope counters as event reads.</summary>
public sealed class InMemoryEventStoreProgressTests
{
    /// <summary>Progress includes audits and other matching streams, while excluding other scopes.</summary>
    [Theory]
    [InlineData("orders", "one", 3UL)]
    [InlineData("orders", null, 5UL)]
    [InlineData(null, null, 6UL)]
    public async Task ShouldReportExactScopeProgressIncludingAudits(string? area, string? resource, ulong head)
    {
        // Arrange
        var store = new InMemoryEventStore();
        await AppendAsync(store, "tenant", "orders", "one", 3);
        await AppendAsync(store, "tenant", "orders", "two", 2);
        await AppendAsync(store, "tenant", "accounts", "one", 1);
        await AppendAsync(store, "other", "orders", "one", 4);
        var position = Cursor(1);

        // Act
        var progress = await store.GetProgressAsync(EventStreamPattern.ForPattern("tenant", area, resource), position);

        // Assert
        Assert.Equal(position, progress.Position);
        Assert.Equal(Cursor(head), progress.SourcePosition);
        Assert.Equal(head - 1, progress.PendingEvents);
        Assert.Equal(progress.PendingEvents, progress.PendingEventsLowerBound);
        Assert.False(progress.IsCaughtUp);

        var caughtUp = await store.GetProgressAsync(EventStreamPattern.ForPattern("tenant", area, resource), Cursor(head));
        Assert.True(caughtUp.IsCaughtUp);
        Assert.Equal(0UL, caughtUp.PendingEvents);
    }

    /// <summary>A checkpoint beyond available source history keeps its invalidity visible.</summary>
    [Fact]
    public async Task ShouldNotTreatACheckpointAheadOfTheSourceAsCaughtUp()
    {
        // Arrange
        var store = new InMemoryEventStore();

        // Act
        var progress = await store.GetProgressAsync(EventStreamPattern.ForPattern("tenant"), Cursor(5));

        // Assert
        Assert.Equal(Cursor(0), progress.SourcePosition);
        Assert.Null(progress.PendingEvents);
        Assert.Null(progress.PendingEventsLowerBound);
        Assert.Null(progress.IsCaughtUp);
    }

    static async ValueTask AppendAsync(InMemoryEventStore store, string realm, string area, string resource, int count)
    {
        var aggregateId = Uuid.CreateVersion4();
        var stream = new EventStreamAddress(realm, area, resource);
        var raisedCount = count - 1;
        if (raisedCount > 0)
        {
            var events = Enumerable.Range(0, raisedCount).Select(index =>
                DomainEventSeed.Attach(new Changed(index), aggregateId, (ulong)index + 1)).ToArray();
            await store.AppendAsync(stream, 0, events);
        }
        var audit = DomainEventSeed.Attach(new Changed(raisedCount), aggregateId, (ulong)raisedCount, isAudit: true);
        await store.AppendAsync(stream, (ulong)raisedCount, [audit]);
    }

    static EventCursor Cursor(ulong position) => new(position.ToString(System.Globalization.CultureInfo.InvariantCulture));
    sealed record Changed(int Value) : DomainEvent;
}
