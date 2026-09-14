using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
///     Covers what a Fitz notice or schedule consumer does with a payload it cannot deserialize.
///     Neither transport redelivers, so a malformed message is lost either way — but losing it must
///     not also end the enumeration and take every later delivery with it.
/// </summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class FitzNotificationPoisonMessageTests
{
    /// <summary>A notice request that did not declare notice delivery is lost before dispatch.</summary>
    [Fact]
    public async Task ShouldDropNoticeGivenDeclaredTransportMismatch()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var consumer = new FitzNoticeRequestConsumer(
            new ScriptedNoticeClient([Envelope(serializer)]), serializer, "notice://test/shared/action",
            TestJson.Catalog(RequestTransportId.Callable, typeof(UniversalAction)));

        Assert.Empty(await ReadAllAsync(consumer));
    }

    /// <summary>A scheduled request that did not declare schedule delivery is lost before dispatch.</summary>
    [Fact]
    public async Task ShouldDropScheduleGivenDeclaredTransportMismatch()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var consumer = new FitzScheduledRequestConsumer(
            new ScriptedScheduleClient([ScheduleEnvelope(serializer)]), serializer,
            "schedule://test/shared/action/run",
            TestJson.Catalog(RequestTransportId.Callable, typeof(UniversalAction)),
            new AllowScheduledRequestActorValidator());

        Assert.Empty(await ReadAllAsync(consumer));
    }

    /// <summary>
    ///     Verifies a notice whose body is not a Portia envelope is skipped and the next notice on
    ///     the same subscription still arrives.
    /// </summary>
    [Fact]
    public async Task ShouldSkipAnUndeserializableNoticeAndKeepReading()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var logger = new CapturingLogger<FitzNoticeRequestConsumer>();
        var consumer = new FitzNoticeRequestConsumer(
            new ScriptedNoticeClient([Encoding.UTF8.GetBytes("not-an-envelope"), Envelope(serializer)]),
            serializer, "notice://test/shared/action",
            TestJson.Catalog(RequestTransportId.Notice, typeof(UniversalAction)), logger);

        var delivered = await ReadAllAsync(consumer);

        _ = Assert.Single(delivered);
        _ = Assert.IsType<UniversalAction>(delivered[0].Request);
        Assert.Equal((1004, LogLevel.Warning), Assert.Single(logger.Entries));
    }

    /// <summary>
    ///     Verifies the same for a fired schedule entry, whose envelope has a second layer that can
    ///     fail independently of the request payload inside it.
    /// </summary>
    [Fact]
    public async Task ShouldSkipAnUndeserializableScheduleEntryAndKeepReading()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var logger = new CapturingLogger<FitzScheduledRequestConsumer>();
        var consumer = new FitzScheduledRequestConsumer(
            new ScriptedScheduleClient([Encoding.UTF8.GetBytes("{}"), ScheduleEnvelope(serializer)]),
            serializer, "schedule://test/shared/action/run",
            TestJson.Catalog(RequestTransportId.Schedule, typeof(UniversalAction)),
            new AllowScheduledRequestActorValidator(), logger);

        var delivered = await ReadAllAsync(consumer);

        _ = Assert.Single(delivered);
        _ = Assert.IsType<UniversalAction>(delivered[0].Request);
        Assert.Equal((1004, LogLevel.Warning), Assert.Single(logger.Entries));
    }

    /// <summary>F2: A retryable notice envelope is currently lost and the next notice continues.</summary>
    [Fact]
    public async Task ShouldRecordLostAndContinueGivenRetryableNoticeEnvelopeFailure()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        using var meter = ListenToLost(out var lost);
        var consumer = new FitzNoticeRequestConsumer(
            new ScriptedNoticeClient([UnsupportedEnvelope(serializer), Envelope(serializer)]), serializer,
            "notice://test/shared/action", TestJson.Catalog(RequestTransportId.Notice, typeof(UniversalAction)));

        var delivered = await ReadAllAsync(consumer);

        _ = Assert.Single(delivered);
        Assert.Equal(["notice"], lost);
    }

    /// <summary>G1-retryable: A retryable scheduled envelope is lost without actor validation and processing continues.</summary>
    [Fact]
    public async Task ShouldRecordLostWithoutValidationAndContinueGivenRetryableScheduledEnvelopeFailure()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var validator = new CountingScheduledValidator();
        using var meter = ListenToLost(out var lost);
        var consumer = new FitzScheduledRequestConsumer(new ScriptedScheduleClient([
                ScheduleEnvelope(UnsupportedEnvelope(serializer)), ScheduleEnvelope(serializer)
            ]), serializer, "schedule://test/shared/action/run",
            TestJson.Catalog(RequestTransportId.Schedule, typeof(UniversalAction)), validator);

        var delivered = await ReadAllAsync(consumer);

        _ = Assert.Single(delivered);
        Assert.Equal(1, validator.Calls);
        Assert.Equal(["schedule"], lost);
    }

    static async Task<List<RequestNotification>> ReadAllAsync(IRequestNotificationConsumer consumer)
    {
        var delivered = new List<RequestNotification>();
        await foreach (var notification in consumer.ReadAsync())
            delivered.Add(notification);
        return delivered;
    }

    static ReadOnlyMemory<byte> Envelope(JsonRequestSerializer serializer) =>
        serializer.Serialize(new UniversalAction(1), null, RequestMetadata.Create(), null);

    static ReadOnlyMemory<byte> ScheduleEnvelope(JsonRequestSerializer serializer) =>
        ScheduleEnvelope(Envelope(serializer));

    static ReadOnlyMemory<byte> ScheduleEnvelope(ReadOnlyMemory<byte> envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new FitzScheduledRequestEnvelope(1, "portia:system", "Portia", envelope.ToArray()),
            FitzJsonContext.Default.FitzScheduledRequestEnvelope);

    static ReadOnlyMemory<byte> UnsupportedEnvelope(JsonRequestSerializer serializer)
    {
        var envelope = JsonNode.Parse(Envelope(serializer).Span)!.AsObject();
        envelope["version"] = 3;
        _ = envelope.Remove("contract");
        return Encoding.UTF8.GetBytes(envelope.ToJsonString());
    }

    static MeterListener ListenToLost(out List<string> transports)
    {
        var captured = new List<string>();
        transports = captured;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == PortiaTelemetry.SourceName &&
                    instrument.Name == "portia.request.delivery.count")
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var values = tags.ToArray();
            if (Equals(values[2].Value, "lost"))
                captured.Add((string)values[1].Value!);
        });
        listener.Start();
        return listener;
    }

    sealed class CountingScheduledValidator : IScheduledRequestActorValidator
    {
        public int Calls { get; private set; }

        public ValueTask<Result<System.Security.Claims.ClaimsPrincipal>> ValidateAsync(string route, string subject,
            string issuer, CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult(Result<System.Security.Claims.ClaimsPrincipal>.Success(RequestActor.System));
        }
    }

    sealed class ScriptedNoticeClient(IReadOnlyList<ReadOnlyMemory<byte>> bodies) : INoticeClient
    {
        public Task PublishAsync(string route, ReadOnlyMemory<byte> body, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<NoticeSubscription> SubscribeAsync(string selector, CancellationToken ct = default) =>
            Task.FromResult(new NoticeSubscription(selector,
                Messages(selector, ct), _ => ValueTask.CompletedTask, Task.CompletedTask));

        async IAsyncEnumerable<NoticeMessage> Messages(string route, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var body in bodies)
            {
                ct.ThrowIfCancellationRequested();
                yield return new NoticeMessage(route, body);
                await Task.Yield();
            }
        }
    }

    sealed class ScriptedScheduleClient(IReadOnlyList<ReadOnlyMemory<byte>> payloads) : IScheduleClient
    {
        public Task<ScheduleSubscription> SubscribeAsync(string selector, CancellationToken ct = default) =>
            Task.FromResult(new ScheduleSubscription(selector,
                Notifications(selector, ct), _ => ValueTask.CompletedTask, Task.CompletedTask));

        public Task<string?> CreateAsync(string route, string cron, ScheduleDeliveryMode mode,
            ReadOnlyMemory<byte> payload, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CancelAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ScheduleListPage> ListAsync(ulong? offset, ulong? limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ScheduleEntry>> ListBySelectorAsync(string selector,
            CancellationToken ct = default) => throw new NotSupportedException();

        async IAsyncEnumerable<ScheduleNotification> Notifications(string route,
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var payload in payloads)
            {
                ct.ThrowIfCancellationRequested();
                yield return new ScheduleNotification(route, payload);
                await Task.Yield();
            }
        }
    }

    sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(int EventId, LogLevel Level)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((eventId.Id, logLevel));
    }
}
