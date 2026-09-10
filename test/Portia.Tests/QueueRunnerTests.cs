using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

/// <summary>
///     Verifies that queued requests dispatch through the same request bus as any other origin.
/// </summary>
public sealed class QueueRunnerTests
{
    /// <summary>
    ///     Verifies that every queued request is dispatched to its handler and completed.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchAndCompleteEveryQueuedRequest()
    {
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var bus = busHost.Bus;
        var items = new[]
        {
            new FakeQueuedRequest(new ChangeValue(1)),
            new FakeQueuedRequest(new ChangeValue(2)),
            new FakeQueuedRequest(new ChangeValue(3))
        };
        var consumer = new FakeQueueConsumer(items);
        var runner = new QueueRunner(consumer, RequestDeliveryScopes.FixedQueue(bus, new TestRequestActorValidator()));

        await runner.RunAsync();

        Assert.Equal(3, handler.LastValue);
        Assert.All(items, item => Assert.True(item.Completed));
        Assert.All(items, item => Assert.False(item.Abandoned));
    }

    /// <summary>
    ///     Verifies that a failed dispatch abandons the request instead of stopping the run, and
    ///     still dispatches the requests that follow it.
    /// </summary>
    [Fact]
    public async Task ShouldAbandonFailedRequestAndContinue()
    {
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var bus = busHost.Bus;
        var failing = new FakeQueuedRequest(new ChangeValue(1), true);
        var succeeding = new FakeQueuedRequest(new ChangeValue(2));
        var consumer = new FakeQueueConsumer([failing, succeeding]);
        var runner = new QueueRunner(consumer, RequestDeliveryScopes.FixedQueue(bus, new TestRequestActorValidator()));

        await runner.RunAsync();

        Assert.False(failing.Completed);
        Assert.True(failing.Abandoned);
        Assert.True(succeeding.Completed);
        Assert.Equal(2, handler.LastValue);
    }

    /// <summary>
    ///     Verifies that a non-transient <see cref="RequestError" /> completes (drops) the request
    ///     instead of abandoning it for redelivery, since it would fail identically again.
    /// </summary>
    [Fact]
    public async Task ShouldCompleteNonTransientFailureInsteadOfAbandoning()
    {
        using var busHost = TestRequestBus.Create();
        var bus = busHost.Bus;
        var invalid = new FakeQueuedRequest(new InvalidChangeValue(1));
        var terminal = new RecordingTerminalHandler();
        var consumer = new FakeQueueConsumer([invalid]);
        var runner = new QueueRunner(consumer,
            RequestDeliveryScopes.FixedQueue(bus, new TestRequestActorValidator(), terminalHandler: terminal));

        await runner.RunAsync();

        var failure = Assert.Single(terminal.Failures);
        Assert.Equal(QueuedRequestTerminalReason.PermanentFailure, failure.Reason);
        Assert.Same(invalid.Request, failure.Request);
        Assert.Same(invalid.Metadata, failure.Metadata);
        Assert.Equal(invalid.Invocation, failure.Invocation);
        Assert.Equal(invalid.Attempt, failure.Attempt);
        Assert.Equal("Value is invalid.", failure.Error?.Message);
        Assert.Null(failure.Exception);
        Assert.True(invalid.Completed);
        Assert.False(invalid.Abandoned);
    }

    /// <summary>A permanent delivery without an application disposition remains transport-owned.</summary>
    [Fact]
    public async Task ShouldFaultWithoutAcknowledgmentGivenPermanentFailureAndMissingHandler()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new InvalidChangeValue(1));
        var runner = new QueueRunner(new FakeQueueConsumer([queued]),
            RequestDeliveryScopes.FixedQueue(busHost.Bus, new TestRequestActorValidator()));

        var failure = await Assert.ThrowsAsync<TerminalHandlerMissingException>(() => runner.RunAsync());

        Assert.Equal(QueuedRequestTerminalReason.PermanentFailure, failure.Reason);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>An actor-validation terminal outcome is also fail-closed without a callback.</summary>
    [Fact]
    public async Task ShouldFaultWithoutAcknowledgmentGivenActorValidationFailureAndMissingHandler()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), actorToken: "expired-token");
        var runner = new QueueRunner(new FakeQueueConsumer([queued]),
            RequestDeliveryScopes.FixedQueue(busHost.Bus, new TestRequestActorValidator("expired-token")));

        var failure = await Assert.ThrowsAsync<TerminalHandlerMissingException>(() => runner.RunAsync());

        Assert.Equal(QueuedRequestTerminalReason.ActorValidationFailure, failure.Reason);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>
    ///     Verifies that a request whose carried actor token fails re-validation — e.g. it expired
    ///     since it was enqueued — is dropped (completed, not redelivered) without ever reaching the
    ///     handler. Retrying would not make an expired token valid, so this must never be abandoned
    ///     for redelivery, and expiry must always be honored even though the request already made it
    ///     onto the queue.
    /// </summary>
    [Fact]
    public async Task ShouldCompleteRequestWithoutDispatchingWhenActorTokenFailsRevalidation()
    {
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var bus = busHost.Bus;
        var expired = new FakeQueuedRequest(new ChangeValue(1), actorToken: "expired-token");
        var terminal = new RecordingTerminalHandler();
        var consumer = new FakeQueueConsumer([expired]);
        var runner = new QueueRunner(consumer,
            RequestDeliveryScopes.FixedQueue(bus, new TestRequestActorValidator("expired-token"),
                terminalHandler: terminal));

        await runner.RunAsync();

        var failure = Assert.Single(terminal.Failures);
        Assert.Equal(QueuedRequestTerminalReason.ActorValidationFailure, failure.Reason);
        Assert.Equal(RequestErrorKind.Unauthorized, failure.Error?.Kind);
        Assert.Null(failure.Exception);
        Assert.True(expired.Completed);
        Assert.False(expired.Abandoned);
        Assert.Null(handler.LastValue);
    }

    /// <summary>
    ///     Verifies that a request whose actor token re-validation fails does not stop the run — the
    ///     requests that follow it still dispatch normally.
    /// </summary>
    [Fact]
    public async Task ShouldContinueAfterActorTokenRevalidationFailure()
    {
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var bus = busHost.Bus;
        var expired = new FakeQueuedRequest(new ChangeValue(1), actorToken: "expired-token");
        var valid = new FakeQueuedRequest(new ChangeValue(2), actorToken: "valid-token");
        var terminal = new RecordingTerminalHandler();
        var consumer = new FakeQueueConsumer([expired, valid]);
        var runner = new QueueRunner(consumer,
            RequestDeliveryScopes.FixedQueue(bus, new TestRequestActorValidator("expired-token"),
                terminalHandler: terminal));

        await runner.RunAsync();

        Assert.True(expired.Completed);
        Assert.True(valid.Completed);
        Assert.Equal(2, handler.LastValue);
    }

    /// <summary>Callback completion precedes the one transport acknowledgment.</summary>
    [Fact]
    public async Task ShouldCompleteCallbackBeforeAcknowledgingTerminalDeliveryExactlyOnce()
    {
        using var busHost = TestRequestBus.Create();
        var operations = new List<string>();
        var queued = new FakeQueuedRequest(new InvalidChangeValue(1), operations: operations);
        var terminal = new RecordingTerminalHandler(operations: operations);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), terminalHandler: terminal));

        await runner.RunAsync();

        Assert.Equal(["terminal", "complete"], operations);
        Assert.Equal(1, queued.CompletionCount);
        Assert.Equal(0, queued.AbandonmentCount);
    }

    /// <summary>A retryable handler result at the configured threshold becomes terminal.</summary>
    [Fact]
    public async Task ShouldInvokeTerminalHandlerGivenRetryableResultAtTerminalAttempt()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new InvalidChangeValue(1, true), attempt: 3);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }, terminal));

        await runner.RunAsync();

        var failure = Assert.Single(terminal.Failures);
        Assert.Equal(QueuedRequestTerminalReason.RetryLimitReached, failure.Reason);
        Assert.True(failure.Error?.IsTransient);
        Assert.Null(failure.Exception);
        Assert.True(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>An unexpected exception at the configured threshold also becomes terminal.</summary>
    [Fact]
    public async Task ShouldInvokeTerminalHandlerGivenUnexpectedFailureAtTerminalAttempt()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), true, attempt: 3);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }, terminal));

        await runner.RunAsync();

        var failure = Assert.Single(terminal.Failures);
        Assert.Equal((uint)3, failure.Attempt);
        Assert.Equal(QueuedRequestTerminalReason.RetryLimitReached, failure.Reason);
        Assert.NotNull(failure.Exception);
        Assert.True(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>A retryable result remains broker-owned until a configured threshold is reached.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(3U)]
    public async Task ShouldAbandonRetryableResultBelowThresholdOrWithoutThreshold(uint? terminalAttempt)
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new InvalidChangeValue(1, true), attempt: 2);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = terminalAttempt },
            terminal));

        await runner.RunAsync();

        Assert.Empty(terminal.Failures);
        Assert.False(queued.Completed);
        Assert.True(queued.Abandoned);
    }

    /// <summary>A terminal callback failure leaves ownership with the transport.</summary>
    [Fact]
    public async Task ShouldLeaveDeliveryUnacknowledgedGivenTerminalHandlerFailureWhenAttemptIsTerminal()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), true, attempt: 3);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 },
            new RecordingTerminalHandler(true)));

        var failure = await Assert.ThrowsAsync<TerminalHandlerFailureException>(() => runner.RunAsync());

        Assert.Equal("terminal failed", failure.InnerException?.Message);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>The hosted transport must not turn a terminal callback fault into a restart loop.</summary>
    [Fact]
    public async Task ShouldFaultHostedRunnerGivenTerminalHandlerFailure()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), true, attempt: 3);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 },
            new RecordingTerminalHandler(true)));
        using var hosted = new QueueRunnerHostedService(runner);

        await hosted.StartAsync(default);
        var failure = await Assert.ThrowsAsync<TerminalHandlerFailureException>(async () =>
            await (hosted.ExecuteTask ?? throw new InvalidOperationException("The hosted runner did not start.")));

        Assert.Equal("terminal failed", failure.InnerException?.Message);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>A configured retry threshold is fail-closed when no terminal handler exists.</summary>
    [Fact]
    public async Task ShouldFaultWithoutAcknowledgmentGivenRetryLimitAndMissingHandler()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), true, attempt: 3);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }));

        var failure = await Assert.ThrowsAsync<TerminalHandlerMissingException>(() => runner.RunAsync());

        Assert.Equal(QueuedRequestTerminalReason.RetryLimitReached, failure.Reason);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>Failures remain retryable below the configured terminal threshold.</summary>
    [Fact]
    public async Task ShouldAbandonGivenFailureBelowRetryLimitAndMissingHandler()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), true, attempt: 2);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }));

        await runner.RunAsync();

        Assert.False(queued.Completed);
        Assert.True(queued.Abandoned);
    }

    /// <summary>The hosted transport must expose a missing terminal handler instead of restarting.</summary>
    [Fact]
    public async Task ShouldFaultHostedRunnerGivenTerminalHandlerMissing()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new InvalidChangeValue(1));
        var runner = new QueueRunner(new FakeQueueConsumer([queued]),
            RequestDeliveryScopes.FixedQueue(busHost.Bus, new TestRequestActorValidator()));
        using var hosted = new QueueRunnerHostedService(runner);

        await hosted.StartAsync(default);
        var failure = await Assert.ThrowsAsync<TerminalHandlerMissingException>(async () =>
            await (hosted.ExecuteTask ?? throw new InvalidOperationException("The hosted runner did not start.")));

        Assert.Equal(QueuedRequestTerminalReason.PermanentFailure, failure.Reason);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>Fitz's combined worker host must propagate both terminal ownership faults.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldFaultFitzWorkerRestartBoundaryGivenTerminalOwnershipFailure(bool missing)
    {
        var expected = missing
            ? (Exception)new TerminalHandlerMissingException(QueuedRequestTerminalReason.PermanentFailure)
            : new TerminalHandlerFailureException(new InvalidOperationException("terminal failed"));
        var attempts = 0;

        var failure = await Assert.ThrowsAsync(expected.GetType(), () => FitzApplicationWorkers.RetryAsync(
            "queue://test/work/item", _ =>
            {
                attempts++;
                return Task.FromException(expected);
            }, TimeSpan.FromSeconds(1), TimeProvider.System, null, default));

        Assert.Same(expected, failure);
        Assert.Equal(1, attempts);
    }

    sealed class FakeQueueConsumer(IReadOnlyList<IQueuedRequest> items) : IRequestQueueConsumer
    {
        public async IAsyncEnumerable<IQueuedRequest> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return item;
            }
        }
    }

    sealed class FakeQueuedRequest(
        IRequest request,
        bool throwOnDispatch = false,
        string? actorToken = "valid-token",
        uint attempt = 1,
        List<string>? operations = null) : IQueuedRequest
    {
        public int CompletionCount { get; private set; }

        public int AbandonmentCount { get; private set; }

        public bool Completed { get; private set; }

        public bool Abandoned { get; private set; }
        public RequestMetadata Metadata { get; } = RequestMetadata.Create();
        public RequestInvocation Invocation => new QueueInvocation("queue://test/work/item", Attempt);

        public IRequest Request { get; } = throwOnDispatch ? new ThrowingChangeValue(0) : request;

        public string? ActorToken { get; } = actorToken;

        public uint Attempt => attempt;

        public ValueTask CompleteAsync(CancellationToken ct = default)
        {
            CompletionCount++;
            Completed = true;
            operations?.Add("complete");
            return ValueTask.CompletedTask;
        }

        public ValueTask AbandonAsync(CancellationToken ct = default)
        {
            AbandonmentCount++;
            Abandoned = true;
            operations?.Add("abandon");
            return ValueTask.CompletedTask;
        }
    }

    sealed class RecordingTerminalHandler(bool throws = false, List<string>? operations = null)
        : IQueuedRequestTerminalHandler
    {
        public List<QueuedRequestFailureContext> Failures { get; } = [];

        public ValueTask HandleAsync(QueuedRequestFailureContext context, CancellationToken ct = default)
        {
            Failures.Add(context);
            operations?.Add("terminal");
            return throws
                ? ValueTask.FromException(new InvalidOperationException("terminal failed"))
                : ValueTask.CompletedTask;
        }
    }

    sealed record ThrowingChangeValue(int Value) : IRequest;

    internal sealed record InvalidChangeValue(int Value, bool IsTransient = false) : IRequest;

    internal sealed class InvalidChangeValueHandler : IRequestHandler<InvalidChangeValue>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<InvalidChangeValue> context, CancellationToken ct) =>
            ValueTask.FromResult(Result.Failure(new RequestError(RequestErrorKind.Validation, "Value is invalid.",
                context.Request.IsTransient)));
    }
}
