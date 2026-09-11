using System.Diagnostics.Metrics;
using System.Text;
using Cntryl.Fitz.Abstractions.Domains.Stream;

namespace Cntryl.Portia;

/// <summary>Verifies Fitz read telemetry reflects iterator termination truthfully.</summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class FitzEventStoreReadTelemetryTests
{
    /// <summary>Natural exhaustion and consumer disposal are successful and count yielded records.</summary>
    [Theory]
    [InlineData(false, false, 2)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 1)]
    public async Task ShouldReportSuccessForCompletionAndEarlyDisposal(bool pattern, bool stopEarly, long expectedCount)
    {
        var measurements = new Measurements();
        using var listener = measurements.Listen();
        var fixture = CreateStore(new Pages(CreatePage(2)));

        if (pattern)
        {
            await foreach (var _ in fixture.Store.ReadAsync(EventStreamPattern.ForPattern("test")))
                if (stopEarly)
                    break;
        }
        else
        {
            await foreach (var _ in fixture.Store.ReadAsync(fixture.Stream))
                if (stopEarly)
                    break;
        }

        Assert.Equal("success", Assert.Single(measurements.Durations).Outcome);
        Assert.Equal(expectedCount, Assert.Single(measurements.Counts));
    }

    /// <summary>Caller cancellation and genuine read failures have distinct outcomes and zero count.</summary>
    [Theory]
    [InlineData(false, true, "canceled")]
    [InlineData(true, true, "canceled")]
    [InlineData(false, false, "fault")]
    [InlineData(true, false, "fault")]
    public async Task ShouldDistinguishCancellationFromFailure(bool pattern, bool cancel, string outcome)
    {
        var measurements = new Measurements();
        using var listener = measurements.Listen();
        using var cancellation = new CancellationTokenSource();
        if (cancel)
            cancellation.Cancel();
        Exception failure = cancel
            ? new OperationCanceledException(cancellation.Token)
            : new IOException("read failed");
        var fixture = CreateStore(new Pages(failure));

        var error = await Record.ExceptionAsync(async () =>
        {
            if (pattern)
            {
                await foreach (var _ in fixture.Store.ReadAsync(EventStreamPattern.ForPattern("test"),
                                   ct: cancellation.Token))
                { }
            }
            else
            {
                await foreach (var _ in fixture.Store.ReadAsync(fixture.Stream, ct: cancellation.Token))
                { }
            }
        });

        if (cancel)
            _ = Assert.IsType<OperationCanceledException>(error);
        else
            _ = Assert.IsType<IOException>(error);
        Assert.Equal(outcome, Assert.Single(measurements.Durations).Outcome);
        Assert.Empty(measurements.Counts);
    }

    static (FitzEventStore Store, EventStreamAddress Stream) CreateStore(IStreamClient pages)
    {
        var serializer = TestJson.DomainSerializer(
            new DomainEventTypeCatalog().Register<ValueChanged>(1, "test.value.changed"));
        return (new FitzEventStore(pages, serializer), new EventStreamAddress("test", "events", "one"));
    }

    static StreamReadPage CreatePage(int count)
    {
        var stream = new EventStreamAddress("test", "events", "one");
        var serializer = TestJson.DomainSerializer(
            new DomainEventTypeCatalog().Register<ValueChanged>(1, "test.value.changed"));
        var items = Enumerable.Range(0, count).Select(index =>
        {
            var ev = new ValueChanged(index);
            ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), Uuid.CreateVersion4(),
                (ulong)index + 1, DateTimeOffset.UtcNow));
            var record = new StreamRecord(stream.ToString(), (ulong)index, (ulong)index, (ulong)index,
                (ulong)index, serializer.Serialize(ev).ToArray(), Encoding.UTF8.GetBytes(stream.ToString()), 0);
            return new StreamReadItem(stream.ToString(), (StreamReadItemKind)0, record, (ulong)index,
                (ulong)index, (ulong)index, null);
        }).ToArray();
        return new StreamReadPage(items,
            new StreamReadCursor((ulong)(count - 1), (ulong)(count - 1), (ulong)(count - 1),
                (ulong)(count - 1), null, null, false));
    }

    sealed class Measurements
    {
        public List<(string Scope, string Outcome)> Durations { get; } = [];
        public List<long> Counts { get; } = [];

        public MeterListener Listen()
        {
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, current) =>
                {
                    if (instrument.Meter.Name == PortiaTelemetry.SourceName &&
                        instrument.Name is "portia.event_store.operation.duration" or
                            "portia.event_store.event.count")
                        current.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
            {
                var values = tags.ToArray();
                if (Equals(values[0].Value, "read"))
                    Durations.Add(((string)values[1].Value!, (string)values[2].Value!));
            });
            listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                if (Equals(tags[0].Value, "read"))
                    Counts.Add(value);
            });
            listener.Start();
            return listener;
        }
    }

    sealed class Pages(Exception? failure = null, StreamReadPage? page = null) : IStreamClient
    {
        readonly Exception? _failure = failure;
        readonly StreamReadPage? _page = page;

        public Pages(StreamReadPage page) : this(null, page) { }

        public Task<StreamReadPage> ReadPageAsync(string route, ulong startOffset, ulong limit = 100,
            StreamFilterSet? filter = null, ulong? maxBytes = null, ulong? cursorFingerprint = null,
            ulong? capturedWatermark = null, CancellationToken ct = default) =>
            _failure is null ? Task.FromResult(_page!) : Task.FromException<StreamReadPage>(_failure);

        public Task<IStreamSession> BeginAsync(string route, ReadOnlyMemory<byte>? ingestMetadata = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamRecord> ReadAsync(string route, ulong startOffset, ulong limit = 100,
            StreamFilterSet? filter = null, ulong? maxBytes = null, ulong? cursorFingerprint = null,
            ulong? capturedWatermark = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StreamRecord?> PeekAsync(string route, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<StreamMetadata> MetadataAsync(string route, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<StreamSubscription> SubscribeAsync(string pattern, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
