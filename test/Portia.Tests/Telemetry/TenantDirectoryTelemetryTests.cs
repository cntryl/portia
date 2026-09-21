using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

/// <summary>Verifies bounded diagnostics for event-sourced tenant discovery.</summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class TenantDirectoryTelemetryTests
{
    static readonly EventStreamPattern Pattern = EventStreamPattern.ForPattern("global", "tenants", "registry");
    static readonly EventStreamAddress Stream = new("global", "tenants", "registry");

    /// <summary>A snapshot reports replay cost, lifecycle volume, and final active membership.</summary>
    [Fact]
    public async Task ShouldMeasureCompleteSnapshotReplayWithoutTenantIdentityTags()
    {
        var clock = new ManualTestClock();
        var source = new TimedReader(clock);
        source.Add(new TenantRegistered("acme"));
        source.Add(new TenantRegistered("globex"));
        source.Add(new TenantDeregistered("acme"));
        using var listener = Listen(out var measurements);
        var directory = CreateDirectory(source, clock);
        var tenants = new List<TenantId>();

        await foreach (var tenant in directory.GetActiveTenantsAsync())
            tenants.Add(tenant);

        Assert.Equal([new TenantId("globex")], tenants);
        AssertMeasurement(measurements, "portia.tenant_directory.operation.duration", 3, "snapshot", "replay");
        AssertMeasurement(measurements, "portia.tenant_directory.event.count", 3, "snapshot", "replay");
        AssertMeasurement(measurements, "portia.tenant_directory.active.count", 1, "snapshot", "replay");
        Assert.DoesNotContain(measurements.SelectMany(measurement => measurement.Tags),
            tag => tag.Key.Contains("tenant", StringComparison.Ordinal) ||
                   tag.Key.Contains("cursor", StringComparison.Ordinal));
    }

    /// <summary>Legacy watch reads distinguish the initial replay from a later catch-up pass.</summary>
    [Fact]
    public async Task ShouldDistinguishLegacyWatchReplayFromCatchUp()
    {
        var clock = new ManualTestClock();
        var source = new TimedReader(clock);
        source.Add(new TenantRegistered("acme"));
        using var listener = Listen(out var measurements);
        var directory = CreateDirectory(source, clock);
        using var cancellation = new CancellationTokenSource();
        await using var changes = directory.WatchAsync(cancellation.Token).GetAsyncEnumerator();

        Assert.True(await changes.MoveNextAsync());
        Assert.Equal(new TenantLifecycleChange(TenantLifecycleChangeKind.Added, new TenantId("acme")),
            changes.Current);

        source.Add(new TenantDeregistered("acme"));
        Assert.True(await changes.MoveNextAsync());
        Assert.Equal(new TenantLifecycleChange(TenantLifecycleChangeKind.Removed, new TenantId("acme")),
            changes.Current);

        var waiting = changes.MoveNextAsync().AsTask();
        Assert.Equal(TimeSpan.FromSeconds(1), await clock.WaitForDelayAsync());
        cancellation.Cancel();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        AssertMeasurement(measurements, "portia.tenant_directory.operation.duration", 1, "watch", "replay");
        AssertMeasurement(measurements, "portia.tenant_directory.event.count", 1, "watch", "replay");
        AssertMeasurement(measurements, "portia.tenant_directory.active.count", 1, "watch", "replay");
        AssertMeasurement(measurements, "portia.tenant_directory.operation.duration", 1, "watch", "catch_up");
        AssertMeasurement(measurements, "portia.tenant_directory.event.count", 1, "watch", "catch_up");
        AssertMeasurement(measurements, "portia.tenant_directory.active.count", 0, "watch", "catch_up");
    }

    /// <summary>A production cursor distinguishes replay from catch-up and excludes consumer delay.</summary>
    [Fact]
    public async Task ShouldMeasureCursorReplayAndCatchUpWithoutConsumerBackpressure()
    {
        var clock = new ManualTestClock();
        var source = new TimedReader(clock);
        source.Add(new TenantRegistered("acme"));
        using var listener = Listen(out var measurements);
        var directory = CreateDirectory(source, clock);
        using var cancellation = new CancellationTokenSource();
        await using var cursor = await directory.OpenCursorAsync(cancellation.Token);
        await using var changes = cursor.ReadAsync(cancellation.Token).GetAsyncEnumerator();

        Assert.True(await changes.MoveNextAsync());
        Assert.Equal(new TenantLifecycleChange(TenantLifecycleChangeKind.Added, new TenantId("acme")),
            changes.Current);

        source.Add(new TenantDeregistered("acme"));
        Assert.True(await changes.MoveNextAsync());
        Assert.Equal(new TenantLifecycleChange(TenantLifecycleChangeKind.Removed, new TenantId("acme")),
            changes.Current);

        clock.Advance(TimeSpan.FromSeconds(30));
        var waiting = changes.MoveNextAsync().AsTask();
        Assert.Equal(TimeSpan.FromSeconds(1), await clock.WaitForDelayAsync());
        cancellation.Cancel();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        AssertMeasurement(measurements, "portia.tenant_directory.operation.duration", 1, "cursor", "replay");
        AssertMeasurement(measurements, "portia.tenant_directory.event.count", 1, "cursor", "replay");
        AssertMeasurement(measurements, "portia.tenant_directory.active.count", 1, "cursor", "replay");
        AssertMeasurement(measurements, "portia.tenant_directory.operation.duration", 1, "cursor", "catch_up");
        AssertMeasurement(measurements, "portia.tenant_directory.event.count", 1, "cursor", "catch_up");
        AssertMeasurement(measurements, "portia.tenant_directory.active.count", 0, "cursor", "catch_up");
        Assert.DoesNotContain(measurements, measurement =>
            measurement is { Name: "portia.tenant_directory.operation.duration", Value: > 1 } &&
            HasTags(measurement, "cursor", "catch_up", "success"));
    }

    /// <summary>A production cursor preserves bounded partial progress when its reader faults.</summary>
    [Fact]
    public async Task ShouldReportPartialCursorReplayProgressGivenReaderFault()
    {
        var clock = new ManualTestClock();
        var source = new TimedReader(clock);
        source.Add(new TenantRegistered("acme"));
        source.Add(new TenantRegistered("poison"));
        source.FailBeforeOffset = 1;
        using var listener = Listen(out var measurements);
        var directory = CreateDirectory(source, clock);
        await using var cursor = await directory.OpenCursorAsync();
        await using var changes = cursor.ReadAsync().GetAsyncEnumerator();

        _ = await Assert.ThrowsAsync<IOException>(() => changes.MoveNextAsync().AsTask());

        AssertMeasurement(measurements, "portia.tenant_directory.operation.duration", 1, "cursor", "replay",
            "fault");
        AssertMeasurement(measurements, "portia.tenant_directory.event.count", 1, "cursor", "replay", "fault");
        AssertMeasurement(measurements, "portia.tenant_directory.active.count", 1, "cursor", "replay", "fault");
    }

    static EventSourcedTenantDirectory<TenantRegistered, TenantDeregistered> CreateDirectory(
        IDomainEventReader source, TimeProvider clock) =>
        new(source, Pattern, GetTenantId, TimeSpan.FromSeconds(1), clock);

    static TenantId GetTenantId(DomainEvent ev) => ev switch
    {
        TenantRegistered registered => new TenantId(registered.TenantId),
        TenantDeregistered deregistered => new TenantId(deregistered.TenantId),
        _ => throw new InvalidOperationException($"Unexpected event type '{ev.GetType()}'.")
    };

    static MeterListener Listen(out ConcurrentBag<Measurement> measurements)
    {
        var captured = new ConcurrentBag<Measurement>();
        measurements = captured;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == PortiaTelemetry.SourceName)
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            captured.Add(new Measurement(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            captured.Add(new Measurement(instrument.Name, value, tags.ToArray())));
        listener.Start();
        return listener;
    }

    static void AssertMeasurement(IEnumerable<Measurement> measurements, string name, double value,
        string operation, string phase, string outcome = "success")
    {
        Assert.Contains(measurements, measurement => measurement.Name == name && measurement.Value == value &&
                                                     HasTags(measurement, operation, phase, outcome));
    }

    static bool HasTags(Measurement measurement, string operation, string phase, string outcome) =>
        measurement.Tags.Any(tag => tag is { Key: "portia.operation", Value: { } } &&
                                            Equals(tag.Value, operation)) &&
        measurement.Tags.Any(tag => tag is { Key: "portia.phase", Value: { } } && Equals(tag.Value, phase)) &&
        measurement.Tags.Any(tag => tag is { Key: "portia.outcome", Value: { } } && Equals(tag.Value, outcome));

    readonly record struct Measurement(string Name, double Value, KeyValuePair<string, object?>[] Tags);

    sealed class TimedReader(ManualTestClock clock) : IDomainEventReader
    {
        readonly List<DomainEventRecord> _records = [];

        public ulong? FailBeforeOffset { get; set; }

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset,
            CancellationToken ct) => throw new NotSupportedException();

        public async IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, EventCursor cursor,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var fromOffset = cursor == EventCursor.Start
                ? 0
                : ulong.Parse(cursor.Value!, CultureInfo.InvariantCulture);
            foreach (var record in _records.Where(record => record.ResourceOffset >= fromOffset).ToArray())
            {
                ct.ThrowIfCancellationRequested();
                if (record.ResourceOffset == FailBeforeOffset)
                    throw new IOException("Unreadable tenant lifecycle event.");
                clock.Advance(TimeSpan.FromSeconds(1));
                yield return record;
            }

            await Task.CompletedTask;
        }

        public void Add(DomainEvent ev)
        {
            var offset = (ulong)_records.Count;
            ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), Uuid.CreateVersion4(), offset + 1,
                clock.GetUtcNow()));
            _records.Add(new DomainEventRecord(Stream, ev, offset,
                new EventCursor((offset + 1).ToString(CultureInfo.InvariantCulture))));
        }
    }
}
