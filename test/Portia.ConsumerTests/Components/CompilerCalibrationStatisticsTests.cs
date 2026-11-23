using Cntryl.Portia.CompilerBenchmarks;

namespace Cntryl.Portia.Consumer;

public sealed class CompilerCalibrationStatisticsTests
{
    [Fact]
    public void ShouldUseWarmMarginGivenNewDriverInWarmWorker()
    {
        // Arrange
        var workers = Workers(10);

        // Act
        var comparisons = Campaign.Compare(workers);

        // Assert
        Assert.Equal(1, Assert.Single(comparisons, row => row.Metric == "median-ms").Allowance);
    }

    [Fact]
    public void ShouldRejectComparisonGivenDifferentHarnessBytes()
    {
        // Arrange
        var workers = Workers(10);

        for (var i = 1; i < workers.Length; i += 2)
            workers[i] = workers[i] with { Report = workers[i].Report! with { HarnessHash = "changed" } };

        // Act

        var error = Record.Exception(() => Campaign.Compare(workers));

        // Assert
        Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public async Task ShouldRejectBoundaryGivenOutOfOrderWorkerMarker()
    {
        // Arrange
        using var reader = new StringReader("{\"RequestCompleted\":1}\n{}\n");

        // Act
        var error = await Record.ExceptionAsync(() => Campaign.ReadWorkerAsync(reader, 1, null, _ => Task.CompletedTask));

        // Assert
        Assert.IsType<InvalidOperationException>(error);
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("preparation")]
    [InlineData("verification")]
    public void ShouldRejectNonfinitePhaseGivenWorkerSample(string phase)
    {
        // Arrange
        var workers = Workers(10);
        var sample = workers[0].Report!.Samples[0];
        workers[0].Report!.Samples[0] = sample with
        {
            CpuMilliseconds = phase == "cpu" ? double.NaN : 1,
            PreparationMilliseconds = phase == "preparation" ? double.NaN : 1,
            VerificationMilliseconds = phase == "verification" ? double.NaN : 1
        };

        // Act
        var error = Record.Exception(() => Campaign.Compare(workers));

        // Assert
        Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public void ShouldDetectCpuRegressionGivenStableWallLatency()
    {
        // Arrange
        var workers = Workers(10);

        // Act
        var comparisons = Campaign.Compare(workers);

        // Assert
        Assert.Equal("regression", Assert.Single(comparisons, row => row.Metric == "cpu-ms").Result);
    }

    [Fact]
    public void ShouldRejectColdTailClaimGivenTenProcessPairs()
    {
        // Arrange
        var workers = Workers(10);

        // Act
        var comparisons = Campaign.CompareFirstRequests(workers);

        // Assert
        Assert.Equal("insufficient-cold-processes", Assert.Single(comparisons, row => row.Metric == "first-request-p95-ms").Result);
    }

    [Fact]
    public void ShouldRejectFirstLoadGivenMissingRequestBoundary()
    {
        // Arrange
        var workers = Workers(10);
        workers[0] = workers[0] with { FirstRequestMilliseconds = null };

        // Act
        var error = Record.Exception(() => Campaign.CompareFirstRequests(workers));

        // Assert
        Assert.IsType<InvalidOperationException>(error);
    }

    static WorkerResult[] Workers(int pairs) => [.. Enumerable.Range(0, pairs).SelectMany(index =>
    {
        var sample = new Sample("probe/all/cold", "minimal", 0, 1, 10, 0, 0, 0, 0,
            new Evidence("same", 1, 1, 0, "probe"));
        var report = new WorkerReport(Campaign.Format, sample.Case, sample.Size, 0, "none", 1, "fixture", "harness",
            "runtime", "roslyn", "os", "arch", new Dictionary<string, string>
            {
                ["harness-il"] = "same", ["fixture-references"] = "same", ["System.Text.Json.SourceGeneration"] = "same"
            }, [sample]);
        return new[]
        {
            new WorkerResult(index, "baseline", "base.dll", 1, report, null, FirstRequestMilliseconds: 100),
            new WorkerResult(index, "candidate", "candidate.dll", 1,
                report with { ProcessId = 2, Samples = [sample with { CpuMilliseconds = 20 }] }, null, FirstRequestMilliseconds: 100)
        };
    })];

    [Fact]
    public async Task ShouldSignalRequestCompletionGivenVerificationHasNotStarted()
    {
        // Arrange
        var notified = false;
        using var operation = new BenchmarkCase(() => ValueTask.CompletedTask,
            () => Task.FromResult(new Evidence("same", 0, 0, 0, notified ? "notified" : "not-notified")));

        // Act
        var sample = await BenchmarkCase.MeasurePreparedAsync(operation, new CaseSpec("probe", "first"), "minimal", 0,
            () => notified = true);

        // Assert
        Assert.Equal("notified", sample.Evidence.Detail);
    }

    [Fact]
    public void ShouldDetectTailRegressionGivenUnchangedMedian()
    {
        // Arrange
        var pairs = Enumerable.Range(0, 10).Select(_ =>
            (Enumerable.Repeat(100.0, 10).ToArray(), Enumerable.Repeat(100.0, 9).Append(200.0).ToArray())).ToArray();

        // Act
        var result = Campaign.CompareP95("tail", pairs);

        // Assert
        Assert.Equal("regression", result.Result);
        Assert.True(result.Lower > result.Allowance);
    }

    [Fact]
    public void ShouldRejectTailClaimGivenTooFewOperations()
    {
        // Arrange
        var pairs = Enumerable.Range(0, 10).Select(_ =>
            (Enumerable.Repeat(100.0, 1).ToArray(), Enumerable.Repeat(100.0, 1).ToArray())).ToArray();

        // Act
        var result = Campaign.CompareP95("tail", pairs);

        // Assert
        Assert.Equal("insufficient-processes-or-samples", result.Result);
    }

    [Theory]
    [InlineData(null, false, 0)]
    [InlineData(1, false, 0)]
    [InlineData(0, true, 0)]
    [InlineData(0, false, 1)]
    public void ShouldRejectThermalOrPowerLimitGivenHostPolicy(int? thermal, bool lowPower, long throttled)
    {
        // Arrange
        var interval = new HostInterval(1, 0, 10, 0, 0, 0, 1, true, thermal, lowPower, throttled);

        // Act
        var result = HostPolicy.Evaluate(interval);

        // Assert
        Assert.False(result.Accepted);
    }

    [Fact]
    public void ShouldReadMonotonicCpuCountersGivenSupportedOperatingSystem()
    {
        // Arrange
        var supported = OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

        // Act
        var first = HostCounters.Capture();
        var second = HostCounters.Capture();

        // Assert
        Assert.Equal(supported, first.Supported);
        Assert.Equal(supported, second.Supported);

        if (supported)
        {
            Assert.True(first.TotalCpu > 0, first.Error);
            Assert.True(second.TotalCpu >= first.TotalCpu, second.Error);
            Assert.True(first.Processors > 0);
            Assert.True(first.IdleCpu <= first.TotalCpu);
        }
    }
}
