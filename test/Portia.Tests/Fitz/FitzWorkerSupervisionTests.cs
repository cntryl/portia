namespace Cntryl.Portia;

/// <summary>Checks listener failure propagation, sibling cleanup, and ordinary worker shutdown.</summary>
public sealed class FitzWorkerSupervisionTests
{
    /// <summary>A fatal listener cancels its healthy sibling and awaits cleanup before reaching the host.</summary>
    [Fact]
    public async Task ShouldAwaitHealthyListenerCleanupBeforePropagatingFatalFailure()
    {
        var expected = new TerminalHandlerFailureException(new IOException("terminal observer failed"));
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = FitzApplicationWorkers.SuperviseAsync([
            async _ =>
            {
                await fail.Task;
                throw expected;
            },
            async token =>
            {
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
            }
        ], default);

        try
        {
            fail.TrySetResult();
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted);
            Assert.False(cleanupFinished.Task.IsCompleted);
            releaseCleanup.TrySetResult();

            var error = await Assert.ThrowsAsync<TerminalHandlerFailureException>(() =>
                run.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Same(expected, error);
            Assert.True(cleanupFinished.Task.IsCompleted);
        }
        finally
        {
            fail.TrySetResult();
            releaseCleanup.TrySetResult();
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TerminalHandlerFailureException)
            {
            }
        }
    }

    /// <summary>The original fatal failure remains visible alongside callback and asynchronous cleanup failures.</summary>
    [Fact]
    public async Task ShouldPreserveFatalAndSiblingCleanupFailures()
    {
        var expected = new TerminalHandlerFailureException(new IOException("terminal observer failed"));
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration callback = default;
        var run = FitzApplicationWorkers.SuperviseAsync([
            async _ =>
            {
                await fail.Task;
                throw expected;
            },
            async token =>
            {
                // Task.Delay can resume this listener before CancelAsync reaches its earlier callback.
                // The outer test owns registration so listener cleanup cannot unregister that callback.
                callback = token.Register(() =>
                    throw new InvalidOperationException("listener cancellation callback failed"));
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
#pragma warning disable CA2219 // Intentionally inject asynchronous listener cleanup failure.
                    throw new IOException("listener asynchronous cleanup failed");
#pragma warning restore CA2219
                }
            }
        ], default);

        try
        {
            fail.TrySetResult();

            var error = await Assert.ThrowsAsync<AggregateException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            var faults = error.Flatten().InnerExceptions;
            Assert.Equal(3, faults.Count);
            Assert.Contains(expected, faults);
            Assert.Contains(faults, fault => fault.Message == "listener cancellation callback failed");
            Assert.Contains(faults, fault => fault.Message == "listener asynchronous cleanup failed");
        }
        finally
        {
            callback.Dispose();
        }
    }

    /// <summary>Stopping a healthy host awaits every listener without inventing a fatal failure.</summary>
    [Fact]
    public async Task ShouldCompleteNormallyAfterHostCancellation()
    {
        var stopped = 0;
        using var lifetime = new CancellationTokenSource();
        var run = FitzApplicationWorkers.SuperviseAsync([ListenAsync, ListenAsync], lifetime.Token);

        await lifetime.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, stopped);

        async Task ListenAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                Interlocked.Increment(ref stopped);
            }
        }
    }

    /// <summary>A later listener's synchronous startup failure still cancels and observes previously started work.</summary>
    [Fact]
    public async Task ShouldObserveEarlierListenersWhenStartupThrows()
    {
        var stopped = false;
        var expected = new QueueConfigurationException("invalid listener configuration");
        var run = FitzApplicationWorkers.SuperviseAsync([
            async token =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    stopped = true;
                }
            },
            _ => throw expected
        ], default);

        var error = await Assert.ThrowsAsync<QueueConfigurationException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Same(expected, error);
        Assert.True(stopped);
    }
}
