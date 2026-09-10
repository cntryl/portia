using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Cntryl.Portia;

/// <summary>End-to-end trace-budget and cardinality regressions.</summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class ObservabilityContractTests
{
    /// <summary>Workload lifecycle instruments publish their exact semantic units.</summary>
    [Fact]
    public void ShouldPublishWorkloadMetricUnits()
    {
        var units = new ConcurrentDictionary<string, string?>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == PortiaTelemetry.SourceName && instrument.Name is "portia.workload.active"
                        or "portia.worker.failure"
                        or "portia.worker.restart")
                {
                    units[instrument.Name] = instrument.Unit;
                }
            }
        };
        listener.Start();
        _ = PortiaTelemetry.Meter;

        Assert.Equal("{workload}", units["portia.workload.active"]);
        Assert.Equal("{failure}", units["portia.worker.failure"]);
        Assert.Equal("{restart}", units["portia.worker.restart"]);
    }

    /// <summary>Local dispatch emits one static span and bounded request measurements.</summary>
    [Fact]
    public async Task ShouldEmitBoundedTelemetryGivenSuccessfulLocalDispatch()
    {
        using var activityListener = ListenToActivities(out var activities);
        using var meterListener = ListenToMeasurements(out var measurements);
        using var host = TestRequestBus.Create();

        _ = await host.Bus.SendAsync(new TelemetrySuccessAction(), RequestActor.System);

        var activity = Assert.Single(activities,
            item => (item.GetTagItem("request.type") as string) == nameof(TelemetrySuccessAction));
        Assert.Equal(PortiaTelemetry.ExecuteActivityName, activity.DisplayName);
        Assert.DoesNotContain(activity.TagObjects, tag => tag.Key.Contains("id", StringComparison.OrdinalIgnoreCase));
        var duration = Assert.Single(measurements,
            item => item.Name == "portia.request.duration" &&
                    item.Tags.Any(tag => Equals(tag.Value, nameof(TelemetrySuccessAction))));
        Assert.Equal(["request.type", "transport", "outcome"], duration.Tags.Select(tag => tag.Key));
    }

    /// <summary>
    ///     A stream denied at authorization reports the denial as its request outcome. The denial
    ///     leaves the method by throwing rather than by completing the enumeration, so it must not be
    ///     counted as an infrastructure fault alongside genuinely broken deliveries.
    /// </summary>
    [Fact]
    public async Task ShouldRecordDenialOutcomeGivenStreamRejectedByAuthorization()
    {
        using var meterListener = ListenToMeasurements(out var measurements);
        using var host = TestRequestBus.Create(permissionEvaluator: TestPermissionEvaluator.DenyAll());

        _ = await Assert.ThrowsAsync<RequestAuthorizationException>(async () =>
        {
            await foreach (var _ in host.Bus.StreamAsync(new TelemetryGuardedSequence(), RequestActor.Anonymous))
            {
            }
        });

        var duration = Assert.Single(measurements, item => item.Name == "portia.request.duration"
                                                           && item.Tags.Any(tag =>
                                                               Equals(tag.Value, nameof(TelemetryGuardedSequence))));
        Assert.Equal("forbidden", Assert.Single(duration.Tags, tag => tag.Key == "outcome").Value);
    }

    /// <summary>Scheduled delivery starts a new trace linked to, rather than parented by, the scheduling trace.</summary>
    [Fact]
    public void ShouldLinkNewTraceGivenScheduledDeliveryContext()
    {
        using var listener = ListenToActivities(out var activities);
        var propagated = new RequestTraceContext("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        using (PortiaTelemetry.StartProcess("Scheduled", "fitz.schedule", propagated, true))
        {
        }

        var activity = Assert.Single(activities, item => (item.GetTagItem("request.type") as string) == "Scheduled");
        Assert.NotEqual(ActivityTraceId.CreateFromString("4bf92f3577b34da6a3ce929d0e0e4736"), activity.TraceId);
        Assert.Equal(ActivityTraceId.CreateFromString("4bf92f3577b34da6a3ce929d0e0e4736"),
            Assert.Single(activity.Links).Context.TraceId);
    }

    static ActivityListener ListenToActivities(out ConcurrentBag<Activity> activities)
    {
        var captured = new ConcurrentBag<Activity>();
        activities = captured;
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PortiaTelemetry.SourceName,
            Sample = static (ref _) => ActivitySamplingResult.AllData,
            ActivityStopped = captured.Add
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    static MeterListener ListenToMeasurements(out ConcurrentBag<Measurement> measurements)
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
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            captured.Add(new Measurement(instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            captured.Add(new Measurement(instrument.Name, tags.ToArray())));
        listener.Start();
        return listener;
    }

    readonly record struct Measurement(string Name, KeyValuePair<string, object?>[] Tags);
}
