using Cntryl.Portia.CompilerBenchmarks;

namespace Cntryl.Portia.Consumer;

public sealed class CompilerHostPolicyTests
{
    [Fact]
    public void ShouldRejectApparentSpeedupGivenSameBinaryCalibrationDrift()
    {
        // Arrange
        var comparisons = new[] { new Comparison("probe", "median-ms", -50, -55, -45, 5, "within-margin") };

        // Act
        var accepted = Campaign.CalibrationAccepted(comparisons);

        // Assert
        Assert.False(accepted);
    }

    [Fact]
    public void ShouldRejectMixedFixtureSizesGivenPairedCampaignReports()
    {
        // Arrange
        var sample = new Sample("probe/all/body", "minimal", 0, 1, 1, 0, 0, 0, 0,
            new Evidence("same", 1, 1, 0, "probe"));
        var report = new WorkerReport(Campaign.Format, sample.Case, sample.Size, 0, "none", 1, "fixture", "harness",
            "runtime", "roslyn", "os", "arch", new Dictionary<string, string>
            { ["harness-il"] = "same", ["fixture-references"] = "same", ["System.Text.Json.SourceGeneration"] = "same" }, [sample]);
        var other = report with { Size = "small", Samples = [sample with { Size = "small" }] };
        var results = new[]
        {
            new WorkerResult(0, "baseline", "base.dll", 1, report, null),
            new WorkerResult(0, "candidate", "candidate.dll", 1, report, null),
            new WorkerResult(1, "baseline", "base.dll", 1, other, null),
            new WorkerResult(1, "candidate", "candidate.dll", 1, other, null)
        };

        // Act
        var exception = Record.Exception(() => Campaign.Compare(results));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void ShouldRejectBusyHostGivenUnrelatedCpuActivity()
    {
        // Arrange
        var interval = new HostInterval(1, .4, 10, 0, 0, 0, 1, true);

        // Act
        var result = HostPolicy.Evaluate(interval);

        // Assert
        Assert.False(result.Accepted);
        Assert.Equal(.4, result.BackgroundCpuFraction, 5);
    }

    [Fact]
    public void ShouldExcludeOwnedCpuGivenBusyBenchmarkWorker()
    {
        // Arrange
        var interval = new HostInterval(1, .8, 10, 7900, 10, 0, 1, true);

        // Act
        var result = HostPolicy.Evaluate(interval);

        // Assert
        Assert.True(result.Accepted);
        Assert.Equal(.009, result.BackgroundCpuFraction, 5);
    }

    [Theory]
    [InlineData(65536, 1, true)]
    [InlineData(0, 2, true)]
    [InlineData(0, 1, false)]
    public void ShouldRejectUnqualifiedHostGivenPressureOrMissingTelemetry(long swap, int pressure, bool supported)
    {
        // Arrange
        var interval = new HostInterval(1, 0, 10, 0, 0, swap, pressure, supported);

        // Act
        var result = HostPolicy.Evaluate(interval);

        // Assert
        Assert.False(result.Accepted);
        Assert.NotEmpty(result.Reasons);
    }
}
