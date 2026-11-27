using Cntryl.Portia.CompilerBenchmarks;

namespace Cntryl.Portia.Consumer;

public sealed class CompilerCancellationProbeTests
{
    [Theory]
    [InlineData("discover")]
    [InlineData("discover-cached")]
    [InlineData("preview")]
    [InlineData("compute")]
    public async Task ShouldVerifyUnchangedWorkspaceGivenCodeFixCancellationProbe(string stage)
    {
        // Arrange
        var spec = new CaseSpec("fixes", stage, "json-add");
        var cancellationToken = TestContext.Current.CancellationToken;

        // Act
        var report = await CancellationProbe.RunAsync(spec, "minimal", 1, 0, cancellationToken);

        // Assert
        var observation = Assert.Single(report.Observations);
        Assert.Null(observation.Error);
        Assert.True(observation.WorkspaceUnchanged);
    }

    [Fact]
    public async Task ShouldReportWorkspacePublicationGivenCanceledComputationWithSideEffect()
    {
        // Arrange
        var published = false;
        Task<BenchmarkCase> Prepare(CancellationToken token) => Task.FromResult(new BenchmarkCase(async () =>
        {
            published = true;
            await Task.Delay(Timeout.Infinite, token);
        }, () => Task.FromResult(new Evidence("probe", 0, 0, 0, "probe")),
            workspaceUnchanged: () => !published));

        // Act
        var observation = await CancellationProbe.RunTrialAsync(Prepare, 0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("canceled-after-entry", observation.Status);
        Assert.False(observation.WorkspaceUnchanged);
        Assert.Contains("changed the workspace", observation.Error, StringComparison.Ordinal);
    }

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
    public async Task ShouldRetainWorkspaceGivenUnresponsiveRequestUntilExecutionEnds()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new ProbeOwner(() => disposed.TrySetResult());
        var operation = new BenchmarkCase(() => new ValueTask(release.Task),
            () => Task.FromResult(new Evidence("probe", 0, 0, 0, "probe")), owner);
        bool disposedAtTimeout;
        CancellationObservation result;

        // Act
        try
        {
            result = await CancellationProbe.RunTrialAsync(_ => Task.FromResult(operation), 0, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            disposedAtTimeout = disposed.Task.IsCompleted;
        }
        finally
        {
            release.TrySetResult();
        }

        // Assert
        Assert.Equal("unresponsive", result.Status);
        Assert.False(disposedAtTimeout);
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ShouldReportOperationFaultGivenRequestThrowsTimeoutAfterCancellation()
    {
        // Arrange
        static Task<BenchmarkCase> Prepare(CancellationToken token) => Task.FromResult(new BenchmarkCase(async () =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("operation deadline");
            }
        }, () => Task.FromResult(new Evidence("probe", 0, 0, 0, "probe"))));

        // Act
        var result = await CancellationProbe.RunTrialAsync(Prepare, 1, CancellationToken.None);

        // Assert
        Assert.Equal("faulted-after-cancel", result.Status);
        Assert.Contains("operation deadline", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldObserveCancellationGivenPendingOperation()
    {
        // Arrange
        static Task<BenchmarkCase> Prepare(CancellationToken token) => Task.FromResult(new BenchmarkCase(
            async () => await Task.Delay(Timeout.Infinite, token),
            () => Task.FromResult(new Evidence("probe", 0, 0, 0, "probe"))));

        // Act
        var result = await CancellationProbe.RunTrialAsync(Prepare, 1, CancellationToken.None);

        // Assert
        Assert.Equal("canceled-after-entry", result.Status);
        Assert.NotNull(result.CancelToEndMilliseconds);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ShouldPreserveCompletionGivenOperationFinishedBeforeCancellation()
    {
        // Arrange
        CancellationToken observed = default;
        Task<BenchmarkCase> Prepare(CancellationToken token)
        {
            observed = token;
            return Task.FromResult(new BenchmarkCase(() => ValueTask.CompletedTask,
                () => Task.FromResult(new Evidence("probe", 0, 0, 0, "probe"))));
        }

        // Act
        var result = await CancellationProbe.RunTrialAsync(Prepare, 10, CancellationToken.None);

        // Assert
        Assert.Equal("completed-before-cancel", result.Status);
        Assert.False(observed.IsCancellationRequested);
    }

    sealed class ProbeOwner(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
