using Cntryl.Portia.CompilerBenchmarks;

namespace Cntryl.Portia.Consumer;

public sealed class CompilerRetentionProbeTests
{
    [Fact]
    public async Task ShouldPreserveCheckpointGivenCanceledSession()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();

        // Act
        var report = await RetentionProbe.RunAsync("minimal", 4, _ => cancellation.Cancel(), cancellation.Token);

        // Assert
        Assert.Equal("canceled", report.Status);
        Assert.Equal(0, report.EditsCompleted);
        Assert.Single(report.Checkpoints);
        Assert.Empty(report.RequestMilliseconds);
    }

    [Fact]
    public async Task ShouldReleaseOldCompilationsGivenRepeatedApplicationEdits()
    {
        // Arrange
        const int edits = 4;

        // Act
        var report = await RetentionProbe.RunAsync("minimal", edits);

        // Assert
        Assert.Equal(edits, report.EditsCompleted);
        Assert.Equal("smoke-passed", report.Status);
        Assert.All(report.Checkpoints, checkpoint => Assert.InRange(checkpoint.RetiredCompilationsAlive, 0, 2));
    }
}
