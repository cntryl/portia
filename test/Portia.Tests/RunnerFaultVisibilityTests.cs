using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Cntryl.Portia;

/// <summary>Verifies background faults use bounded metrics and never create root spans.</summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class RunnerFaultVisibilityTests
{
    /// <summary>Unexpected swallowed failures remain measurable without an orphan trace.</summary>
    [Fact]
    public void ShouldRecordBoundedMetricWithoutActivityGivenBackgroundFault()
    {
        var activities = new ConcurrentBag<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PortiaTelemetry.SourceName,
            Sample = static (ref _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(activityListener);

        var measurements = new List<(long Value, KeyValuePair<string, object?>[] Tags)>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PortiaTelemetry.SourceName && instrument.Name == "portia.worker.failure")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            measurements.Add((value, tags.ToArray())));
        meterListener.Start();

        PortiaTelemetry.RecordRunnerFault("QueueRunner", RunnerFaultStage.Execution,
            new InvalidOperationException("secret"));

        Assert.Empty(activities);
        var (Value, Tags) = Assert.Single(measurements);
        Assert.Equal(1, Value);
        Assert.Equal(["runner", "stage"], Tags.Select(tag => tag.Key));
        Assert.Equal("execution", Tags[1].Value);
        Assert.DoesNotContain(Tags, tag => Equals(tag.Value, nameof(InvalidOperationException)));
        Assert.DoesNotContain(Tags, tag => Equals(tag.Value, "secret"));
    }

    internal sealed record RunnerFaultAction : IRequest;

    internal sealed class RunnerFaultActionHandler : IRequestHandler<RunnerFaultAction>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<RunnerFaultAction> context, CancellationToken ct) =>
            ValueTask.FromResult(Result.Success);
    }
}
