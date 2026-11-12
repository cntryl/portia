namespace Cntryl.Portia;

/// <summary>Checks that one cancellation callback fault cannot abandon revoked work.</summary>
public sealed class SingleProcessWorkloadCancellationTests
{
    /// <summary>The ownership lifetime still includes asynchronous cleanup when cancellation callbacks fail.</summary>
    /// <param name="throwOnCancellation">Whether an application callback faults during cancellation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldAwaitRevokedWorkloadGivenCancellationCallbackFailure(bool throwOnCancellation)
    {
        // Arrange
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration callback = default;
        WorkloadIdentity[] snapshot = [new("pass2-cancellation-cleanup")];
        using var lifetime = new CancellationTokenSource();
        var coordinator = new SingleProcessWorkloadCoordinator(TimeSpan.FromMilliseconds(1));

        // Act
        var run = coordinator.RunAsync(() => Volatile.Read(ref snapshot), async (_, token) =>
        {
            // Keep registration alive through coordination, even if the cancelled delay resumes
            // cleanup before CancelAsync reaches this callback.
            callback = token.Register(() =>
            {
                callbackRan.TrySetResult();
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

        // Assert
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref snapshot, []);
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await callbackRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var observationWindow = Task.Delay(TimeSpan.FromMilliseconds(100));
            _ = await Task.WhenAny(run, observationWindow);

            Assert.False(run.IsCompleted,
                "A revoked workload is still cleaning up, so coordination must not finish yet.");
            Assert.False(cleanupFinished.Task.IsCompleted);
        }
        finally
        {
            try
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
            finally
            {
                callback.Dispose();
            }
        }
    }

    /// <summary>Cancellation callback and asynchronous cleanup faults both survive revocation.</summary>
    [Fact]
    public async Task ShouldPreserveBothFailuresGivenCancellationAndWorkloadCleanupFailures()
    {
        // Arrange
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        CancellationTokenRegistration callback = default;
        WorkloadIdentity[] snapshot = [new("faulting"), new("sibling")];
        using var lifetime = new CancellationTokenSource();
        var coordinator = new SingleProcessWorkloadCoordinator(TimeSpan.FromMilliseconds(1));

        // Act
        var run = coordinator.RunAsync(() => Volatile.Read(ref snapshot), async (identity, token) =>
        {
            // The test owns the faulting registration until coordination finishes. Disposing it
            // inside the workload could remove it before CancelAsync invokes it.
            if (identity.Name == "faulting")
                callback = token.Register(static () =>
                    throw new InvalidOperationException("cancellation callback failed"));
            if (Interlocked.Increment(ref starts) == 2)
                started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                if (identity.Name == "faulting")
                {
                    cleanupStarted.TrySetResult();
                    await releaseCleanup.Task;
#pragma warning disable CA2219 // Intentionally inject asynchronous workload cleanup failure.
                    throw new IOException("asynchronous workload cleanup failed");
#pragma warning restore CA2219
                }
                siblingStopped.TrySetResult();
            }
        }, lifetime.Token);

        // Assert
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref snapshot, [new("sibling")]);
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseCleanup.TrySetResult();
            var error = await Assert.ThrowsAsync<AggregateException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(["asynchronous workload cleanup failed", "cancellation callback failed"],
                error.Flatten().InnerExceptions.Select(failure => failure.Message).Order(StringComparer.Ordinal));
            Assert.True(siblingStopped.Task.IsCompleted);
        }
        finally
        {
            try
            {
                releaseCleanup.TrySetResult();
                try
                {
                    await lifetime.CancelAsync();
                }
                catch (AggregateException error) when (error.Flatten().InnerExceptions.Any(failure =>
                    failure.Message == "cancellation callback failed"))
                {
                }
                try
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception error) when (error is AggregateException or OperationCanceledException)
                {
                }
            }
            finally
            {
                callback.Dispose();
            }
        }
    }
}
