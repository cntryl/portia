using System.Diagnostics.Metrics;

namespace Cntryl.Portia;

/// <summary>
///     Verifies the bucket boundaries Portia's duration histograms advise. A collector that is
///     given no advice falls back to its own default boundaries, which start at 5 and run to 10,000
///     — sensible for milliseconds and useless for the seconds these instruments record, since
///     every ordinary measurement then lands in the first bucket and no percentile is recoverable.
/// </summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class PortiaTelemetryHistogramAdviceTests
{
    /// <summary>Every histogram Portia publishes states the buckets its unit needs.</summary>
    [Fact]
    public void ShouldAdviseBucketBoundariesForEveryPublishedDurationHistogram()
    {
        var advised = new List<string>();
        var unadvised = new List<string>();

        // Touching the meter creates its instruments, so they exist to be published to the
        // listener below rather than being created lazily after it starts.
        PortiaTelemetry.RequestFinished(PortiaTelemetry.StartTimestamp(), "advice-probe", "test", "success");

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name != PortiaTelemetry.SourceName || instrument is not Histogram<double> histogram)
                return;

            (histogram.Advice?.HistogramBucketBoundaries is { Count: > 0 } ? advised : unadvised)
                .Add(instrument.Name);
        };
        listener.Start();

        Assert.NotEmpty(advised);
        Assert.True(unadvised.Count == 0,
            $"Histograms published without bucket boundaries: {string.Join(", ", unadvised.Order(StringComparer.Ordinal))}.");
    }
}
