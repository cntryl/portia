namespace Cntryl.Portia.Tests;

public sealed class SdkCancellationProbeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRecordCancellationGivenStartedPublication(bool interruptPublication)
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "portia-probe-cancellation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var cancellation = new CancellationTokenSource();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Task.CompletedTask;

        async Task PublishStartedAsync(CancellationToken ct)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "started"), "started", CancellationToken.None);
            published.TrySetResult();
            if (interruptPublication)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        try
        {
            // Act
            operation = SdkCancellationProbe.WaitForCancellationAsync(directory, PublishStartedAsync,
                cancellation.Token);
            await published.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();
            var exception = await Record.ExceptionAsync(() => operation);

            // Assert
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
            Assert.True(File.Exists(Path.Combine(directory, "canceled")));
        }
        finally
        {
            await cancellation.CancelAsync();
            _ = await Record.ExceptionAsync(() => operation);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldPreserveFailureGivenStartedPublicationFault(bool unrelatedCancellation)
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "portia-probe-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Exception failure = unrelatedCancellation
            ? new OperationCanceledException(new CancellationToken(canceled: true))
            : new IOException("Marker publication failed.");

        try
        {
            // Act
            var exception = await Record.ExceptionAsync(() => SdkCancellationProbe.WaitForCancellationAsync(directory,
                _ => Task.FromException(failure), CancellationToken.None));

            // Assert
            Assert.Same(failure, exception);
            Assert.False(File.Exists(Path.Combine(directory, "canceled")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
