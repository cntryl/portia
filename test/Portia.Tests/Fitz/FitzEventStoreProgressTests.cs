using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

/// <summary>Verifies bounded source-progress observations without replaying business payloads.</summary>
public sealed class FitzEventStoreProgressTests
{
    /// <summary>Generated JSON preserves opaque cursor values, start positions, and absent source positions.</summary>
    [Theory]
    [InlineData("7", "12")]
    [InlineData("opaque/processor:v1", "opaque/source:v2")]
    [InlineData(null, null)]
    [InlineData(null, "")]
    public void ShouldRoundTripProgressCursorsThroughGeneratedJson(string? position, string? sourcePosition)
    {
        // Arrange
        var expected = new EventStreamProgress(position is null ? EventCursor.Start : new EventCursor(position),
            sourcePosition is null ? null : sourcePosition.Length == 0 ? EventCursor.Start : new EventCursor(sourcePosition),
            5, false, new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero), 5);
        var json = JsonSerializer.Serialize(expected, EventStreamProgressJsonContext.Default.EventStreamProgress);

        // Act
        var actual = JsonSerializer.Deserialize(json, EventStreamProgressJsonContext.Default.EventStreamProgress);

        // Assert
        Assert.Equal(expected, actual);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(position ?? string.Empty, document.RootElement.GetProperty("position").GetString());
        if (sourcePosition is null)
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("source_position").ValueKind);
        else
            Assert.Equal(sourcePosition, document.RootElement.GetProperty("source_position").GetString());
    }

    /// <summary>Realm and area observations use their own offset coordinates.</summary>
    [Theory]
    [InlineData("orders", 10UL, 12UL)]
    [InlineData(null, 30UL, 32UL)]
    public async Task ShouldObserveThePatternScopeWithoutDeserializingEvents(string? area, ulong position, ulong head)
    {
        // Arrange
        var pattern = EventStreamPattern.ForPattern("tenant", area);
        var client = new ProgressClient(Page(false, Item(3, 10, 30), Item(4, 11, 31)));

        // Act
        var progress = await Store(client).GetProgressAsync(pattern, Cursor(position));

        // Assert
        Assert.Equal(Cursor(position), progress.Position);
        Assert.Equal(Cursor(head), progress.SourcePosition);
        Assert.Equal(2UL, progress.PendingEvents);
        Assert.Equal(progress.PendingEvents, progress.PendingEventsLowerBound);
        Assert.False(progress.IsCaughtUp);
        Assert.Equal((pattern.ToString(), position, 1024UL), Assert.Single(client.Reads));
        Assert.Equal(256UL * 1024, Assert.Single(client.ByteBudgets));
    }

    /// <summary>An unexhausted page proves a lower bound without inventing a total backlog or head.</summary>
    [Fact]
    public async Task ShouldPreserveUnknownHeadForAnUnexhaustedPatternPage()
    {
        // Arrange
        var client = new ProgressClient(Page(true, Item(3, 10, 30), Item(4, 11, 31)));

        // Act
        var progress = await Store(client).GetProgressAsync(EventStreamPattern.ForPattern("tenant", "orders"), Cursor(10));

        // Assert
        Assert.Null(progress.SourcePosition);
        Assert.Null(progress.PendingEvents);
        Assert.Equal(2UL, progress.PendingEventsLowerBound);
        Assert.False(progress.IsCaughtUp);
        Assert.Single(client.Reads);
    }

    /// <summary>An empty read alone cannot validate a checkpoint ahead of the source.</summary>
    [Fact]
    public async Task ShouldPreserveUnknownStatusWhenAnEmptyPatternCannotValidateTheCheckpoint()
    {
        // Arrange
        var client = new ProgressClient(EmptyPage(100), previousPage: EmptyPage(99));

        // Act
        var progress = await Store(client).GetProgressAsync(EventStreamPattern.ForPattern("tenant"), Cursor(100));

        // Assert
        Assert.Null(progress.SourcePosition);
        Assert.Null(progress.PendingEvents);
        Assert.Null(progress.PendingEventsLowerBound);
        Assert.Null(progress.IsCaughtUp);
    }

    /// <summary>A bounded preceding-position probe validates a caught-up checkpoint.</summary>
    [Fact]
    public async Task ShouldValidateAnEmptyPatternsCheckpointAgainstItsPrecedingRecord()
    {
        // Arrange
        var client = new ProgressClient(EmptyPage(3), previousPage: Page(false, Item(2, 2, 2)));

        // Act
        var progress = await Store(client).GetProgressAsync(EventStreamPattern.ForPattern("tenant"), Cursor(3));

        // Assert
        Assert.Equal(Cursor(3), progress.SourcePosition);
        Assert.Equal(0UL, progress.PendingEvents);
        Assert.Equal(0UL, progress.PendingEventsLowerBound);
        Assert.True(progress.IsCaughtUp);
        Assert.Equal(2, client.Reads.Count);
        Assert.Equal(1UL, client.Reads[1].Limit);
        Assert.All(client.ByteBudgets, budget => Assert.Equal(256UL * 1024, budget));
    }

    /// <summary>Expiry skips source offsets without producing withheld-entry markers.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldPreserveSourceProgressWhenExpiredOffsetsPrecedeReadableRecords(bool resource)
    {
        // Arrange
        var pattern = EventStreamPattern.ForPattern("tenant", "orders", resource ? "one" : null);
        var client = new ProgressClient(Page(false, Item(2, 2, 2)), new StreamMetadata(0, 2, 3));

        // Act
        var progress = await Store(client).GetProgressAsync(pattern, EventCursor.Start);

        // Assert
        Assert.Equal(Cursor(3), progress.SourcePosition);
        Assert.Null(progress.PendingEvents);
        Assert.Equal(1UL, progress.PendingEventsLowerBound);
        Assert.False(progress.IsCaughtUp);
    }

    /// <summary>An exhausted page can advance across expired history without returning an event.</summary>
    [Fact]
    public async Task ShouldNotInventAZeroHeadWhenAnEmptyPageCursorAdvanced()
    {
        // Arrange
        var client = new ProgressClient(EmptyPage(8));

        // Act
        var progress = await Store(client).GetProgressAsync(EventStreamPattern.ForPattern("tenant", "orders"), EventCursor.Start);

        // Assert
        Assert.Equal(Cursor(9), progress.SourcePosition);
        Assert.Null(progress.PendingEvents);
        Assert.Equal(0UL, progress.PendingEventsLowerBound);
        Assert.False(progress.IsCaughtUp);
    }

    /// <summary>A later resource page cannot inflate the lower bound beyond its sampled metadata frontier.</summary>
    [Fact]
    public async Task ShouldExcludeConcurrentAppendsBeyondTheSampledResourceHead()
    {
        // Arrange
        var client = new ProgressClient(Page(false, Item(1, 1, 1), Item(2, 2, 2)), new StreamMetadata(0, 1, 2));

        // Act
        var progress = await Store(client).GetProgressAsync(EventStreamPattern.ForPattern("tenant", "orders", "one"), EventCursor.Start);

        // Assert
        Assert.Equal(Cursor(2), progress.SourcePosition);
        Assert.Null(progress.PendingEvents);
        Assert.Equal(1UL, progress.PendingEventsLowerBound);
        Assert.False(progress.IsCaughtUp);
    }

    /// <summary>Offset gaps do not disable validation of later duplicate source positions.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectDuplicatePositionsAfterAnExpiredGap(bool hasMore)
    {
        // Arrange
        var client = new ProgressClient(Page(hasMore, Item(2, 2, 2), Item(2, 2, 2)));

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await Store(client).GetProgressAsync(EventStreamPattern.ForPattern("tenant", "orders"), EventCursor.Start));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>A withheld entry does not bypass scope validation of later records.</summary>
    [Fact]
    public async Task ShouldRejectRecordsOutsideThePatternAfterAWithheldEntry()
    {
        // Arrange
        var withheld = new StreamReadItem("stream://tenant/orders/one", StreamReadItemKind.Filtered, Offset: 0);

        // Act
        var client = new ProgressClient(Page(false, withheld, Item(1, 1, 1, "stream://other/orders/one")));

        // Assert
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Store(client).GetProgressAsync(EventStreamPattern.ForPattern("tenant", "orders"), EventCursor.Start));
    }

    /// <summary>Withheld positions do not become a fabricated readable-event count.</summary>
    [Fact]
    public async Task ShouldNotCountWithheldEntriesAsUnreadEvents()
    {
        // Arrange
        var withheld = new StreamReadItem("stream://tenant/orders/one", StreamReadItemKind.Filtered, Offset: 10);
        var client = new ProgressClient(Page(false, withheld, Item(4, 11, 31)));

        // Act
        var progress = await Store(client).GetProgressAsync(EventStreamPattern.ForPattern("tenant", "orders"), Cursor(10));

        // Assert
        Assert.Equal(Cursor(12), progress.SourcePosition);
        Assert.Null(progress.PendingEvents);
        Assert.Equal(1UL, progress.PendingEventsLowerBound);
        Assert.False(progress.IsCaughtUp);
    }

    /// <summary>A resource's metadata supplies its head; a bounded read verifies its readable backlog.</summary>
    [Fact]
    public async Task ShouldUseMetadataAndBoundedRecordsForResourceProgress()
    {
        // Arrange
        var client = new ProgressClient(Page(false, Item(2, 10, 30), Item(3, 11, 31), Item(4, 12, 32)),
            new StreamMetadata(0, 4, 5));
        var pattern = EventStreamPattern.ForPattern("tenant", "orders", "one");

        // Act
        var progress = await Store(client).GetProgressAsync(pattern, Cursor(2));

        // Assert
        Assert.Equal(Cursor(5), progress.SourcePosition);
        Assert.Equal(3UL, progress.PendingEvents);
        Assert.Equal(3UL, progress.PendingEventsLowerBound);
        Assert.False(progress.IsCaughtUp);
        Assert.Equal(pattern.ToString(), Assert.Single(client.MetadataRoutes));
        Assert.Equal((pattern.ToString(), 2UL, 3UL), Assert.Single(client.Reads));
        Assert.Equal(256UL * 1024, Assert.Single(client.ByteBudgets));
    }

    /// <summary>A large resource backlog remains bounded while preserving its known head.</summary>
    [Fact]
    public async Task ShouldKeepTheResourceHeadWhenItsBacklogExceedsTheProbe()
    {
        // Arrange
        var client = new ProgressClient(Page(true, Item(0, 0, 0), Item(1, 1, 1)), new StreamMetadata(0, 9999, 10000));

        // Act
        var progress = await Store(client).GetProgressAsync(EventStreamPattern.ForPattern("tenant", "orders", "one"), Cursor(0));

        // Assert
        Assert.Equal(Cursor(10000), progress.SourcePosition);
        Assert.Null(progress.PendingEvents);
        Assert.Equal(2UL, progress.PendingEventsLowerBound);
        Assert.Equal(1024UL, Assert.Single(client.Reads).Limit);
    }

    /// <summary>A source behind the supplied checkpoint must not be called caught up.</summary>
    [Fact]
    public async Task ShouldPreserveUnknownStatusForAResourceCheckpointBeyondTheSource()
    {
        // Arrange
        var client = new ProgressClient(Page(false), new StreamMetadata(0, 2, 3));

        // Act
        var progress = await Store(client).GetProgressAsync(EventStreamPattern.ForPattern("tenant", "orders", "one"), Cursor(5));

        // Assert
        Assert.Equal(Cursor(3), progress.SourcePosition);
        Assert.Null(progress.PendingEvents);
        Assert.Null(progress.IsCaughtUp);
        Assert.Empty(client.Reads);
    }

    /// <summary>Retained suffixes cannot claim an exact count across missing source history.</summary>
    [Fact]
    public async Task ShouldPreserveUnknownCountWhenResourceRetentionPassedTheCheckpoint()
    {
        // Arrange
        var client = new ProgressClient(Page(false), new StreamMetadata(3, 4, 2));

        // Act
        var progress = await Store(client).GetProgressAsync(EventStreamPattern.ForPattern("tenant", "orders", "one"), Cursor(1));

        // Assert
        Assert.Equal(Cursor(5), progress.SourcePosition);
        Assert.Null(progress.PendingEvents);
        Assert.False(progress.IsCaughtUp);
        Assert.Empty(client.Reads);
    }

    /// <summary>Scope and cursor validation precede transport access.</summary>
    [Fact]
    public async Task ShouldRejectUnboundPatternsAndForeignCursorsBeforeCallingFitz()
    {
        // Arrange
        var client = new ProgressClient(Page(false));

        // Act
        var store = Store(client);

        // Assert
        _ = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.GetProgressAsync(EventStreamPattern.ForTenant("orders"), EventCursor.Start));
        _ = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.GetProgressAsync(EventStreamPattern.ForPattern("tenant"), new EventCursor("other-reader")));
        Assert.Empty(client.Reads);
        Assert.Empty(client.MetadataRoutes);
    }

    /// <summary>Caller cancellation is preserved and never invokes the transport.</summary>
    [Fact]
    public async Task ShouldRespectCancellationBeforeObservingTheSource()
    {
        // Arrange
        var client = new ProgressClient(Page(false));
        using var cancellation = new CancellationTokenSource();

        // Act
        cancellation.Cancel();

        // Assert
        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Store(client).GetProgressAsync(EventStreamPattern.ForPattern("tenant"), EventCursor.Start, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(client.Reads);
    }

    /// <summary>Even a lower-bound probe must not include records from another tenant or source pattern.</summary>
    [Fact]
    public async Task ShouldRejectRecordsOutsideTheObservedPattern()
    {
        // Arrange
        var client = new ProgressClient(Page(true, Item(0, 0, 0, "stream://other/orders/one")));

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await Store(client).GetProgressAsync(EventStreamPattern.ForPattern("tenant"), EventCursor.Start));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(exception);
    }

    static FitzEventStore Store(IStreamClient client) => new(client, new UnusedSerializer());
    static EventCursor Cursor(ulong position) => new(position.ToString(System.Globalization.CultureInfo.InvariantCulture));

    static StreamReadItem Item(ulong resource, ulong area, ulong realm, string route = "stream://tenant/orders/one")
    {
        var record = new StreamRecord(route, resource, area, realm, null, new byte[] { 255 }, null, 0);
        return new StreamReadItem(route, StreamReadItemKind.Event, record);
    }

    static StreamReadPage Page(bool hasMore, params StreamReadItem[] items)
    {
        var last = items.LastOrDefault()?.Record;
        return new StreamReadPage(items, new StreamReadCursor(last?.Offset ?? 0, last?.AreaOffset,
            last?.RealmOffset, null, null, null, hasMore));
    }

    static StreamReadPage EmptyPage(ulong coveredOffset) => new([], new StreamReadCursor(
        coveredOffset, coveredOffset, coveredOffset, null, null, null, false));

    sealed class UnusedSerializer : IDomainEventSerializer
    {
        public ReadOnlyMemory<byte> Serialize(DomainEvent ev) => throw new InvalidOperationException("A progress probe must not serialize events.");
        public DomainEvent Deserialize(ReadOnlyMemory<byte> data) => throw new InvalidOperationException("A progress probe must not deserialize events.");
    }

    sealed class ProgressClient(StreamReadPage page, StreamMetadata? metadata = null,
        StreamReadPage? previousPage = null) : IStreamClient
    {
        public List<(string Route, ulong Offset, ulong Limit)> Reads { get; } = [];
        public List<string> MetadataRoutes { get; } = [];
        public List<ulong?> ByteBudgets { get; } = [];

        public Task<StreamReadPage> ReadPageAsync(string route, ulong startOffset, ulong limit = 100,
            StreamFilterSet? filter = null, ulong? maxBytes = null, ulong? cursorFingerprint = null,
            ulong? capturedWatermark = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Reads.Add((route, startOffset, limit));
            ByteBudgets.Add(maxBytes);
            return Task.FromResult(Reads.Count > 1 ? previousPage ?? page : page);
        }

        public Task<StreamMetadata> MetadataAsync(string route, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            MetadataRoutes.Add(route);
            return Task.FromResult(metadata ?? throw new InvalidOperationException("Unexpected metadata call."));
        }

        public Task<IStreamSession> BeginAsync(string route, ReadOnlyMemory<byte>? ingestMetadata = null, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamRecord> ReadAsync(string route, ulong startOffset, ulong limit = 100, StreamFilterSet? filter = null, ulong? maxBytes = null, ulong? cursorFingerprint = null, ulong? capturedWatermark = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StreamRecord?> PeekAsync(string route, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StreamSubscription> SubscribeAsync(string pattern, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(EventStreamProgress))]
sealed partial class EventStreamProgressJsonContext : JsonSerializerContext
{
}
