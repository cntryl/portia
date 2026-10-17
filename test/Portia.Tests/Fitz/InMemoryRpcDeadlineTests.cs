namespace Cntryl.Portia;

/// <summary>In-memory RPC calls honor Fitz budgets and cancellation independently of worker cooperation.</summary>
public sealed class InMemoryRpcDeadlineTests
{
    const string Route = "rpc://test/deadline";
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    /// <summary>Invalid budgets fail when the call is created, before lazy enumeration begins.</summary>
    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(86400001)]
    public void ShouldRejectInvalidBudgetsGivenTimeoutOutsideTheFitzRange(int milliseconds)
    {
        var rpc = new InMemoryRpcClient();

        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty, TimeSpan.FromMilliseconds(milliseconds));
        });
    }

    /// <summary>A zero budget expires before any registered worker receives the call.</summary>
    [Fact]
    public async Task ShouldAvoidDispatchGivenZeroBudget()
    {
        var rpc = new InMemoryRpcClient();
        var dispatched = 0;
        await using var worker = await rpc.RegisterWorkerAsync(Route, (_, _, _) =>
        {
            Interlocked.Increment(ref dispatched);
            return ValueTask.CompletedTask;
        });
        var call = rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty, TimeSpan.Zero);

        var failure = await Record.ExceptionAsync(() => ConsumeAsync(call).WaitAsync(HangGuard));

        Assert.Equal(0, dispatched);
        _ = Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(RpcCancellationOutcome.RequestNotSent, await call.Cancellation.WaitAsync(HangGuard));
    }

    /// <summary>Both caller and consumer tokens cancel a lazy call before a worker is invoked.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldAvoidDispatchGivenCancellationBeforeEnumeration(bool useConsumerToken)
    {
#pragma warning disable CA1859 // Exercise token propagation through the published Fitz interface.
        IRpcClient rpc = new InMemoryRpcClient();
#pragma warning restore CA1859
        var dispatched = 0;
        await using var worker = await rpc.RegisterWorkerAsync(Route, (_, _, _) =>
        {
            Interlocked.Increment(ref dispatched);
            return ValueTask.CompletedTask;
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var call = rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty,
            useConsumerToken ? CancellationToken.None : cancellation.Token);
        Assert.False(call.Cancellation.IsCompleted);

        var failure = await Record.ExceptionAsync(() =>
            ConsumeAsync(call, useConsumerToken ? cancellation.Token : CancellationToken.None).WaitAsync(HangGuard));

        Assert.Equal(0, dispatched);
        _ = Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(RpcCancellationOutcome.RequestNotSent, await call.Cancellation.WaitAsync(HangGuard));
    }

    /// <summary>A deadline ends the caller's wait even when the worker ignores its cancelled token.</summary>
    [Fact]
    public async Task ShouldStopAtBudgetGivenWorkerIgnoringCancellation()
    {
        var rpc = new InMemoryRpcClient();
        var probe = new IgnoringWorker();
        await using var worker = await rpc.RegisterWorkerAsync(Route, probe.HandleAsync);
        var call = rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty, TimeSpan.FromSeconds(1));
        var consumption = ConsumeAsync(call);

        try
        {
            await probe.Started.Task.WaitAsync(HangGuard);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumption.WaitAsync(HangGuard));
            await probe.CancellationObserved.Task.WaitAsync(HangGuard);
            Assert.Equal(RpcCancellationOutcome.Forwarded, await call.Cancellation.WaitAsync(HangGuard));
            Assert.False(probe.Completed.Task.IsCompleted);
        }
        finally
        {
            probe.Release.SetResult();
            await probe.Completed.Task.WaitAsync(HangGuard);
            _ = await Record.ExceptionAsync(() => consumption.WaitAsync(HangGuard));
        }
    }

    /// <summary>Expiry before or after dispatch never reports normal completion for an unfinished worker.</summary>
    [Fact]
    public async Task ShouldReportCancellationGivenConcurrentExpiredCallsWithoutTerminalResponse()
    {
        var rpc = new InMemoryRpcClient();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = await rpc.RegisterWorkerAsync(Route, (_, _, _) => new ValueTask(release.Task));
        var calls = Enumerable.Range(0, 64)
            .Select(_ => rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty, TimeSpan.FromMilliseconds(1)))
            .ToArray();
        var consumption = Task.WhenAll(calls.Select(async call =>
        {
            var failure = await Record.ExceptionAsync(() => ConsumeAsync(call));
            return (Failure: failure, Cancellation: await call.Cancellation);
        }));

        try
        {
            var results = await consumption.WaitAsync(HangGuard);

            foreach (var result in results)
            {
                _ = Assert.IsAssignableFrom<OperationCanceledException>(result.Failure);
                Assert.Contains(result.Cancellation,
                    new[] { RpcCancellationOutcome.RequestNotSent, RpcCancellationOutcome.Forwarded });
            }
        }
        finally
        {
            release.SetResult();
            _ = await Record.ExceptionAsync(() => consumption.WaitAsync(HangGuard));
        }
    }

    /// <summary>Caller and WithCancellation tokens stop waiting without requiring worker cooperation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldStopWaitingGivenCancelledCallAndIgnoringWorker(bool useConsumerToken)
    {
        var rpc = new InMemoryRpcClient();
        var probe = new IgnoringWorker();
        await using var worker = await rpc.RegisterWorkerAsync(Route, probe.HandleAsync);
        using var cancellation = new CancellationTokenSource();
        var call = rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty,
            useConsumerToken ? CancellationToken.None : cancellation.Token);
        var consumption = ConsumeAsync(call, useConsumerToken ? cancellation.Token : CancellationToken.None);

        try
        {
            await probe.Started.Task.WaitAsync(HangGuard);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumption.WaitAsync(HangGuard));
            await probe.CancellationObserved.Task.WaitAsync(HangGuard);
            Assert.Equal(RpcCancellationOutcome.Forwarded, await call.Cancellation.WaitAsync(HangGuard));
            Assert.False(probe.Completed.Task.IsCompleted);
        }
        finally
        {
            probe.Release.SetResult();
            await probe.Completed.Task.WaitAsync(HangGuard);
            _ = await Record.ExceptionAsync(() => consumption.WaitAsync(HangGuard));
        }
    }

    /// <summary>Either token can cancel a call when its original and consumer tokens are both present.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldCombineCancellationGivenSeparateCallAndConsumerTokens(bool cancelConsumer)
    {
        var rpc = new InMemoryRpcClient();
        var probe = new IgnoringWorker();
        await using var worker = await rpc.RegisterWorkerAsync(Route, probe.HandleAsync);
        using var callerCancellation = new CancellationTokenSource();
        using var consumerCancellation = new CancellationTokenSource();
        var call = rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty, callerCancellation.Token);
        var consumption = ConsumeAsync(call, consumerCancellation.Token);

        try
        {
            await probe.Started.Task.WaitAsync(HangGuard);
            (cancelConsumer ? consumerCancellation : callerCancellation).Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumption.WaitAsync(HangGuard));
            await probe.CancellationObserved.Task.WaitAsync(HangGuard);
            Assert.Equal(RpcCancellationOutcome.Forwarded, await call.Cancellation.WaitAsync(HangGuard));
            Assert.False((cancelConsumer ? callerCancellation : consumerCancellation).IsCancellationRequested);
            Assert.False(probe.Completed.Task.IsCompleted);
        }
        finally
        {
            probe.Release.SetResult();
            await probe.Completed.Task.WaitAsync(HangGuard);
            _ = await Record.ExceptionAsync(() => consumption.WaitAsync(HangGuard));
        }
    }

    /// <summary>Exceptions from worker cancellation callbacks cannot escape through caller cancellation.</summary>
    [Fact]
    public async Task ShouldIsolateCancellationGivenThrowingWorkerCallback()
    {
        var rpc = new InMemoryRpcClient();
        var callbackRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new IgnoringWorker(() =>
        {
            callbackRan.SetResult();
            throw new InvalidOperationException("worker cancellation callback failed");
        });
        await using var worker = await rpc.RegisterWorkerAsync(Route, probe.HandleAsync);
        using var cancellation = new CancellationTokenSource();
        var call = rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty, cancellation.Token);
        var consumption = ConsumeAsync(call);
        var stop = Task.CompletedTask;

        try
        {
            await probe.Started.Task.WaitAsync(HangGuard);
            stop = Task.Run(() => cancellation.Cancel(), CancellationToken.None);

            await stop.WaitAsync(HangGuard);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumption.WaitAsync(HangGuard));
            await callbackRan.Task.WaitAsync(HangGuard);
            Assert.Equal(RpcCancellationOutcome.Forwarded, await call.Cancellation.WaitAsync(HangGuard));
            Assert.False(probe.Completed.Task.IsCompleted);
        }
        finally
        {
            probe.Release.SetResult();
            await probe.Completed.Task.WaitAsync(HangGuard);
            _ = await Record.ExceptionAsync(() => stop.WaitAsync(HangGuard));
            _ = await Record.ExceptionAsync(() => consumption.WaitAsync(HangGuard));
        }
    }

    /// <summary>Successful and faulted calls settle their cancellation task without requesting cancellation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldCompleteCancellationGivenTerminalWorkerCall(bool failWorker)
    {
        var rpc = new InMemoryRpcClient();
        await using var worker = await rpc.RegisterWorkerAsync(Route, (_, writer, ct) => failWorker
            ? ValueTask.FromException(new InvalidOperationException("worker failed"))
            : writer.SendAsync(new byte[] { 42 }, true, ct));
        var call = rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty);
        Assert.False(call.Cancellation.IsCompleted);

        if (failWorker)
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ConsumeAsync(call).WaitAsync(HangGuard));
            Assert.Equal("worker failed", failure.Message);
        }
        else
        {
            await ConsumeAsync(call).WaitAsync(HangGuard);
        }

        Assert.Equal(RpcCancellationOutcome.NotRequested, await call.Cancellation.WaitAsync(HangGuard));
    }

    /// <summary>A worker's independent cancellation fault does not report caller-requested cancellation.</summary>
    [Fact]
    public async Task ShouldKeepCancellationNotRequestedGivenUnrelatedWorkerCancellation()
    {
        var rpc = new InMemoryRpcClient();
        using var callerCancellation = new CancellationTokenSource();
        using var workerCancellation = new CancellationTokenSource();
        workerCancellation.Cancel();
        await using var worker = await rpc.RegisterWorkerAsync(Route, (_, _, _) =>
            ValueTask.FromException(new OperationCanceledException(workerCancellation.Token)));
        var call = rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty, callerCancellation.Token);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConsumeAsync(call).WaitAsync(HangGuard));

        Assert.False(callerCancellation.IsCancellationRequested);
        Assert.Equal(RpcCancellationOutcome.NotRequested, await call.Cancellation.WaitAsync(HangGuard));
    }

    /// <summary>Failure to locate a worker also settles cancellation without hanging.</summary>
    [Fact]
    public async Task ShouldCompleteCancellationGivenMissingWorker()
    {
        var rpc = new InMemoryRpcClient();
        var call = rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => ConsumeAsync(call).WaitAsync(HangGuard));

        Assert.Equal(RpcCancellationOutcome.NotRequested, await call.Cancellation.WaitAsync(HangGuard));
    }

    /// <summary>The concrete API exposes lazy call handles and accepts the maximum one-day budget.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldExposeLazyRpcCallGivenConcreteClient(bool useMaximumBudget)
    {
        var rpc = new InMemoryRpcClient();
        var dispatched = 0;
        TimeSpan? receivedBudget = null;
        await using var worker = await rpc.RegisterWorkerAsync(Route, (request, writer, ct) =>
        {
            Interlocked.Increment(ref dispatched);
            receivedBudget = request.RemainingTime;
            return writer.SendAsync(new byte[] { 42 }, true, ct);
        });
        var call = useMaximumBudget
            ? rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty, TimeSpan.FromDays(1))
            : rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty);
        _ = Assert.IsType<RpcCall>(call);
        Assert.Equal(0, dispatched);
        Assert.False(call.Cancellation.IsCompleted);
        await using var enumerator = call.GetAsyncEnumerator();
        Assert.Equal(0, dispatched);

        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(HangGuard));
        Assert.Equal(new byte[] { 42 }, enumerator.Current.Body.ToArray());
        Assert.False(await enumerator.MoveNextAsync().AsTask().WaitAsync(HangGuard));

        Assert.Equal(1, dispatched);
        if (useMaximumBudget)
        {
            Assert.NotNull(receivedBudget);
            Assert.InRange(receivedBudget.Value, TimeSpan.Zero, TimeSpan.FromDays(1));
        }
        else
        {
            Assert.Null(receivedBudget);
        }
        Assert.Equal(RpcCancellationOutcome.NotRequested, await call.Cancellation.WaitAsync(HangGuard));
    }

    /// <summary>Leaving a stream after a nonfinal frame forwards cancellation to its still-running worker.</summary>
    [Fact]
    public async Task ShouldForwardCancellationGivenEarlyStreamDisposal()
    {
        var rpc = new InMemoryRpcClient();
        var probe = new IgnoringWorker();
        await using var worker = await rpc.RegisterWorkerAsync(Route, probe.HandleStreamingAsync);
        var call = rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty);
        var enumerator = call.GetAsyncEnumerator();
        var firstMove = enumerator.MoveNextAsync().AsTask();
        var disposed = false;

        try
        {
            Assert.True(await firstMove.WaitAsync(HangGuard));
            Assert.Equal(new byte[] { 42 }, enumerator.Current.Body.ToArray());
            await enumerator.DisposeAsync().AsTask().WaitAsync(HangGuard);
            disposed = true;

            await probe.CancellationObserved.Task.WaitAsync(HangGuard);
            Assert.Equal(RpcCancellationOutcome.Forwarded, await call.Cancellation.WaitAsync(HangGuard));
            Assert.False(probe.Completed.Task.IsCompleted);
        }
        finally
        {
            probe.Release.SetResult();
            await probe.Completed.Task.WaitAsync(HangGuard);
            _ = await Record.ExceptionAsync(() => firstMove.WaitAsync(HangGuard));
            if (!disposed)
                await enumerator.DisposeAsync().AsTask().WaitAsync(HangGuard);
        }
    }

    /// <summary>Blocking worker callbacks cannot hold either caller cancellation or early stream disposal.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldStopWithoutWaitingGivenBlockingWorkerCallback(bool disposeStream)
    {
        var rpc = new InMemoryRpcClient();
        using var callbackRelease = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new IgnoringWorker(() =>
        {
            callbackEntered.SetResult();
            try
            {
                callbackRelease.Wait(CancellationToken.None);
            }
            finally
            {
                callbackExited.SetResult();
            }
        });
        await using var worker = await rpc.RegisterWorkerAsync(Route, probe.HandleStreamingAsync);
        using var cancellation = new CancellationTokenSource();
        var call = rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty, cancellation.Token);
        var enumerator = call.GetAsyncEnumerator();
        var firstMove = enumerator.MoveNextAsync().AsTask();
        Task<bool>? nextMove = null;
        var stop = Task.CompletedTask;
        var disposed = false;

        try
        {
            Assert.True(await firstMove.WaitAsync(HangGuard));
            stop = Task.Run(async () =>
            {
                if (disposeStream)
                    await enumerator.DisposeAsync();
                else
                    cancellation.Cancel();
            }, CancellationToken.None);
            await stop.WaitAsync(HangGuard);
            disposed = disposeStream;

            await callbackEntered.Task.WaitAsync(HangGuard);
            if (!disposeStream)
            {
                nextMove = enumerator.MoveNextAsync().AsTask();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nextMove.WaitAsync(HangGuard));
            }
            Assert.Equal(RpcCancellationOutcome.Forwarded, await call.Cancellation.WaitAsync(HangGuard));
            Assert.False(callbackExited.Task.IsCompleted);
            Assert.False(probe.Completed.Task.IsCompleted);
        }
        finally
        {
            callbackRelease.Set();
            probe.Release.SetResult();
            _ = await Record.ExceptionAsync(() => stop.WaitAsync(HangGuard));
            _ = await Record.ExceptionAsync(() => firstMove.WaitAsync(HangGuard));
            if (nextMove is not null)
                _ = await Record.ExceptionAsync(() => nextMove.WaitAsync(HangGuard));
            if (!disposed)
                await enumerator.DisposeAsync().AsTask().WaitAsync(HangGuard);
            await probe.Completed.Task.WaitAsync(HangGuard);
        }
    }

    /// <summary>A final frame completes a call normally even while the handler remains busy.</summary>
    [Fact]
    public async Task ShouldCompleteWithoutCancellationGivenFinalFrameAndBusyWorker()
    {
        var rpc = new InMemoryRpcClient();
        var probe = new IgnoringWorker();
        await using var worker = await rpc.RegisterWorkerAsync(Route, probe.HandleFinalThenWaitAsync);
        var call = rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty);
        var consumption = ReadFramesAsync(call);

        try
        {
            await probe.Started.Task.WaitAsync(HangGuard);
            var frames = await consumption.WaitAsync(HangGuard);

            Assert.Equal(new byte[] { 42 }, Assert.Single(frames).Body.ToArray());
            Assert.Equal(RpcCancellationOutcome.NotRequested, await call.Cancellation.WaitAsync(HangGuard));
            Assert.False(probe.CancellationObserved.Task.IsCompleted);
            Assert.False(probe.Completed.Task.IsCompleted);
        }
        finally
        {
            probe.Release.SetResult();
            await probe.Completed.Task.WaitAsync(HangGuard);
            _ = await Record.ExceptionAsync(() => consumption.WaitAsync(HangGuard));
        }
    }

    /// <summary>A worker that blocks synchronously cannot prevent its caller from cancelling.</summary>
    [Fact]
    public async Task ShouldStopWaitingGivenSynchronousWorkerIgnoringCancellation()
    {
        var rpc = new InMemoryRpcClient();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = await rpc.RegisterWorkerAsync(Route, (_, _, ct) =>
        {
            try
            {
                using var registration = ct.Register(() => cancellationObserved.TrySetResult());
                started.SetResult();
                release.Wait(CancellationToken.None);
                return ValueTask.CompletedTask;
            }
            finally
            {
                completed.SetResult();
            }
        });
        var call = rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty, cancellation.Token);
        var consumption = Task.Run(() => ConsumeAsync(call));

        try
        {
            await started.Task.WaitAsync(HangGuard);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumption.WaitAsync(HangGuard));
            await cancellationObserved.Task.WaitAsync(HangGuard);

            Assert.Equal(RpcCancellationOutcome.Forwarded, await call.Cancellation.WaitAsync(HangGuard));
            Assert.False(completed.Task.IsCompleted);
        }
        finally
        {
            release.Set();
            await completed.Task.WaitAsync(HangGuard);
            _ = await Record.ExceptionAsync(() => consumption.WaitAsync(HangGuard));
        }
    }

    /// <summary>Sending a response transfers its bytes before the worker reuses its buffer.</summary>
    [Fact]
    public async Task ShouldPreserveEachFrameGivenReusedWorkerBuffer()
    {
        var rpc = new InMemoryRpcClient();
        var reused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = await rpc.RegisterWorkerAsync(Route, async (_, writer, ct) =>
        {
            var buffer = new byte[] { 1 };
            await writer.SendAsync(buffer, false, ct);
            buffer[0] = 2;
            await writer.SendAsync(buffer, true, ct);
            buffer[0] = 3;
            reused.SetResult();
        });
        var call = rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty);

        var frames = await ReadFramesAsync(call).WaitAsync(HangGuard);
        await reused.Task.WaitAsync(HangGuard);

        Assert.Equal(2, frames.Count);
        Assert.Equal(new byte[] { 1 }, frames[0].Body.ToArray());
        Assert.Equal(new byte[] { 2 }, frames[1].Body.ToArray());
        Assert.Equal(RpcCancellationOutcome.NotRequested, await call.Cancellation.WaitAsync(HangGuard));
    }

    /// <summary>Only an empty final control frame is omitted from the response sequence.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldDistinguishEmptyResponseDataGivenFrameFinality(bool finishWithEmptyFrame)
    {
        var rpc = new InMemoryRpcClient();
        await using var worker = await rpc.RegisterWorkerAsync(Route, async (_, writer, ct) =>
        {
            await writer.SendAsync(new byte[] { 42 }, false, ct);
            await writer.SendAsync(ReadOnlyMemory<byte>.Empty, finishWithEmptyFrame, ct);
            if (!finishWithEmptyFrame)
                await writer.SendAsync(new byte[] { 43 }, true, ct);
        });
        var call = rpc.CallAsync(Route, ReadOnlyMemory<byte>.Empty);

        var frames = await ReadFramesAsync(call).WaitAsync(HangGuard);

        if (finishWithEmptyFrame)
        {
            Assert.Equal(new byte[] { 42 }, Assert.Single(frames).Body.ToArray());
        }
        else
        {
            Assert.Equal(3, frames.Count);
            Assert.Equal(new byte[] { 42 }, frames[0].Body.ToArray());
            Assert.Empty(frames[1].Body.ToArray());
            Assert.Equal(new byte[] { 43 }, frames[2].Body.ToArray());
        }
        Assert.Equal(RpcCancellationOutcome.NotRequested, await call.Cancellation.WaitAsync(HangGuard));
    }

    /// <summary>Lazy dispatch snapshots request bytes before the caller reuses them after a final response.</summary>
    [Fact]
    public async Task ShouldPreserveDispatchedRequestGivenCallerBufferReuseAfterCompletion()
    {
        var rpc = new InMemoryRpcClient();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var atDispatch = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterCompletion = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = await rpc.RegisterWorkerAsync(Route, async (request, writer, ct) =>
        {
            try
            {
                atDispatch.SetResult(request.Body.Span[0]);
                await writer.SendAsync(new byte[] { 42 }, true, ct);
                await release.Task;
                afterCompletion.SetResult(request.Body.Span[0]);
            }
            finally
            {
                completed.SetResult();
            }
        });
        var buffer = new byte[] { 1 };
        var call = rpc.CallAsync(Route, buffer);
        buffer[0] = 2;
        var consumption = ReadFramesAsync(call);

        try
        {
            var frames = await consumption.WaitAsync(HangGuard);
            Assert.Equal(new byte[] { 42 }, Assert.Single(frames).Body.ToArray());
            Assert.Equal(2, await atDispatch.Task.WaitAsync(HangGuard));
            Assert.False(completed.Task.IsCompleted);

            buffer[0] = 3;
            release.SetResult();

            Assert.Equal(2, await afterCompletion.Task.WaitAsync(HangGuard));
            Assert.Equal(RpcCancellationOutcome.NotRequested, await call.Cancellation.WaitAsync(HangGuard));
        }
        finally
        {
            release.TrySetResult();
            await completed.Task.WaitAsync(HangGuard);
            _ = await Record.ExceptionAsync(() => consumption.WaitAsync(HangGuard));
        }
    }

    static async Task ConsumeAsync(RpcCall call, CancellationToken ct = default)
    {
        await foreach (var _ in call.WithCancellation(ct))
        {
        }
    }

    static async Task<List<RpcResponseFrame>> ReadFramesAsync(RpcCall call)
    {
        var frames = new List<RpcResponseFrame>();
        await foreach (var frame in call)
            frames.Add(frame);
        return frames;
    }

    sealed class IgnoringWorker(Action? cancellationCallback = null)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask HandleAsync(RpcRequest request, IRpcResponseWriter writer, CancellationToken ct) =>
            HandleAsync(writer, null, ct);

        public ValueTask HandleStreamingAsync(RpcRequest request, IRpcResponseWriter writer, CancellationToken ct) =>
            HandleAsync(writer, false, ct);

        public ValueTask HandleFinalThenWaitAsync(RpcRequest request, IRpcResponseWriter writer, CancellationToken ct) =>
            HandleAsync(writer, true, ct);

        async ValueTask HandleAsync(IRpcResponseWriter writer, bool? firstFrameFinal, CancellationToken ct)
        {
            try
            {
                using var registration = ct.Register(() =>
                {
                    CancellationObserved.TrySetResult();
                    cancellationCallback?.Invoke();
                });
                Started.SetResult();
                if (firstFrameFinal is { } isFinal)
                    await writer.SendAsync(new byte[] { 42 }, isFinal, CancellationToken.None);
                await Release.Task;
                if (firstFrameFinal != true)
                    await writer.SendAsync(new byte[] { 42 }, true, CancellationToken.None);
            }
            finally
            {
                Completed.SetResult();
            }
        }
    }
}
