using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Cntryl.Portia;

/// <summary>
///     A fired schedule entry a consumer cannot translate is dropped rather than dispatched, so the
///     runner fault it records is what tells an operator the route has a broken entry.
/// </summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class FitzNotificationFaultTests
{
    /// <summary>Verifies a pre-envelope schedule entry is dropped and counted as a validation fault.</summary>
    [Fact]
    public async Task ShouldReportLegacyScheduleEntriesAsAValidationFault()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var legacy = serializer.Serialize(new UniversalAction(1), "legacy-bearer-token", RequestMetadata.Create(),
            null);

        Assert.Equal([("unknown", "schedule", "lost")], await DrainAsync(serializer, legacy));
    }

    /// <summary>Verifies an envelope without a complete system identity is dropped and counted the same way.</summary>
    [Fact]
    public async Task ShouldReportAnIncompleteSystemIdentityAsAValidationFault()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var request = serializer.Serialize(new UniversalAction(1), null, RequestMetadata.Create(), null);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new FitzScheduledRequestEnvelope(1, "   ", "Portia", request.ToArray()),
            FitzJsonContext.Default.FitzScheduledRequestEnvelope);

        Assert.Equal([("unknown", "schedule", "lost")], await DrainAsync(serializer, payload));
    }

    /// <summary>
    ///     Verifies the legacy entry's diagnostic still names the route and the action an operator
    ///     has to take, now that it is reported rather than thrown at the caller.
    /// </summary>
    [Fact]
    public void ShouldNameTheRouteAndRemedyInTheLegacyScheduleDiagnostic()
    {
        var exception = new LegacyScheduledRequestException("schedule://test/shared/action/run");

        Assert.Contains("Cancel and recreate", exception.Message, StringComparison.Ordinal);
        Assert.Contains("schedule://test/shared/action/run", exception.Message, StringComparison.Ordinal);
    }

    static async Task<List<(string Request, string Transport, string Outcome)>> DrainAsync(
        IRequestDeserializer serializer,
        ReadOnlyMemory<byte> payload)
    {
        var lost = new List<(string, string, string)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PortiaTelemetry.SourceName &&
                instrument.Name == "portia.request.delivery.count")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var values = tags.ToArray();
            lock (lost)
                lost.Add(((string)values[0].Value!, (string)values[1].Value!, (string)values[2].Value!));
        });
        listener.Start();
        var consumer = new FitzScheduledRequestConsumer(new OnePayloadScheduleClient(payload), serializer,
            "schedule://test/shared/action/run");

        await foreach (var _ in consumer.ReadAsync())
            Assert.Fail("A schedule entry that cannot be translated must not be dispatched.");

        listener.RecordObservableInstruments();
        lock (lost)
            return [.. lost];
    }

    sealed class OnePayloadScheduleClient(ReadOnlyMemory<byte> payload) : IScheduleClient
    {
        public Task<ScheduleSubscription> SubscribeAsync(string selector, CancellationToken ct = default) =>
            Task.FromResult(new ScheduleSubscription(selector, Notifications(selector, ct),
                _ => ValueTask.CompletedTask, Task.CompletedTask));

        public Task<string?> CreateAsync(string route, string cron, ScheduleDeliveryMode mode,
            ReadOnlyMemory<byte> body, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CancelAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ScheduleListPage> ListAsync(ulong? offset, ulong? limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ScheduleEntry>> ListBySelectorAsync(string selector,
            CancellationToken ct = default) => throw new NotSupportedException();

        async IAsyncEnumerable<ScheduleNotification> Notifications(string route,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new ScheduleNotification(route, payload);
        }
    }
}
