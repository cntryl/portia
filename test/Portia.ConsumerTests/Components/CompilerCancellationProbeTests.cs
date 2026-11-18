using Cntryl.Portia.CompilerBenchmarks;

namespace Cntryl.Portia.Consumer;

public sealed class CompilerCancellationProbeTests
{
    [Theory]
    [InlineData("generators", "body", "all")]
    [InlineData("analyzers", "full", "all")]
    [InlineData("fixes", "compute", "partial")]
    public async Task ShouldHonorCancellationGivenPreparedCompilerRequest(string kind, string stage, string component)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        using var operation = await BenchmarkCase.PrepareAsync(new CaseSpec(kind, stage, component), "minimal", cancellation.Token);
        await cancellation.CancelAsync();

        // Act
        var error = await Record.ExceptionAsync(() => operation.ExecuteAsync().AsTask());

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(error);
    }

    [Fact]
    public async Task ShouldObserveCancellationGivenPendingOperation()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        using var operation = new BenchmarkCase(async () => await Task.Delay(Timeout.Infinite, cancellation.Token),
            () => Task.FromResult(new Evidence("probe", 0, 0, 0, "probe")));

        // Act
        var result = await CancellationProbe.ObserveAsync(operation, cancellation);

        // Assert
        Assert.Equal("canceled-after-entry", result.Status);
        Assert.NotNull(result.CancelToEndMilliseconds);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ShouldPreserveCompletionGivenOperationFinishedBeforeCancellation()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        using var operation = new BenchmarkCase(() => ValueTask.CompletedTask,
            () => Task.FromResult(new Evidence("probe", 0, 0, 0, "probe")));

        // Act
        var result = await CancellationProbe.ObserveAsync(operation, cancellation, 10);

        // Assert
        Assert.Equal("completed-before-cancel", result.Status);
        Assert.False(cancellation.IsCancellationRequested);
    }
}
