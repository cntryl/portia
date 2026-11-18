using Cntryl.Portia.CompilerBenchmarks;

namespace Cntryl.Portia.Consumer;

public sealed class CompilerCalibrationStatisticsTests
{
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
