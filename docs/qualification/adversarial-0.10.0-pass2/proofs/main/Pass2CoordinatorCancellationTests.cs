namespace Cntryl.Portia;

/// <summary>Checks that one cancellation callback fault cannot abandon revoked work.</summary>
public sealed class Pass2CoordinatorCancellationTests
{
    /// <summary>The ownership lifetime still includes asynchronous cleanup when cancellation callbacks fail.</summary>
    /// <param name="throwOnCancellation">Whether an application callback faults during cancellation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldAwaitRevokedWorkloadDespiteCancellationCallbackFailure(bool throwOnCancellation)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkloadIdentity[] snapshot = [new("pass2-cancellation-cleanup")];
        using var lifetime = new CancellationTokenSource();
        var coordinator = new SingleProcessWorkloadCoordinator(TimeSpan.FromMilliseconds(1));
        var run = coordinator.RunAsync(() => Volatile.Read(ref snapshot), async (_, token) =>
        {
            using var callback = token.Register(() =>
            {
                if (throwOnCancellation)
                    throw new InvalidOperationException("application cancellation callback failed");
            });
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                cleanupStarted.TrySetResult();
                await releaseCleanup.Task;
                cleanupFinished.TrySetResult();
            }
        }, lifetime.Token);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref snapshot, []);
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var observationWindow = Task.Delay(TimeSpan.FromMilliseconds(100));
            _ = await Task.WhenAny(run, observationWindow);

            Assert.False(run.IsCompleted,
                "A revoked workload is still cleaning up, so coordination must not finish yet.");
            Assert.False(cleanupFinished.Task.IsCompleted);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await cleanupFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await lifetime.CancelAsync();
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
            catch (AggregateException error) when (throwOnCancellation && error.Flatten().InnerExceptions
                .Any(exception => exception.Message == "application cancellation callback failed"))
            {
            }
        }
    }
}
