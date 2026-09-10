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
        var consumer = new FakeQueueConsumer([invalid]);
        var runner = new QueueRunner(consumer, RequestDeliveryScopes.FixedQueue(bus, new TestRequestActorValidator()));

        await runner.RunAsync();

        Assert.True(invalid.Completed);
        Assert.False(invalid.Abandoned);
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
        var consumer = new FakeQueueConsumer([expired]);
        var runner = new QueueRunner(consumer,
            RequestDeliveryScopes.FixedQueue(bus, new TestRequestActorValidator("expired-token")));

        await runner.RunAsync();

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
        var consumer = new FakeQueueConsumer([expired, valid]);
        var runner = new QueueRunner(consumer,
            RequestDeliveryScopes.FixedQueue(bus, new TestRequestActorValidator("expired-token")));

        await runner.RunAsync();

        Assert.True(expired.Completed);
        Assert.True(valid.Completed);
        Assert.Equal(2, handler.LastValue);
    }

    /// <summary>A successful terminal callback permits acknowledgment.</summary>
    [Fact]
    public async Task ShouldInvokeTerminalHandlerAndCompleteGivenRetryableFailureAtTerminalAttempt()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), true, attempt: 3);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }, terminal));

        await runner.RunAsync();

        var failure = Assert.Single(terminal.Failures);
        Assert.Equal((uint)3, failure.Attempt);
        Assert.NotNull(failure.Exception);
        Assert.True(queued.Completed);
        Assert.False(queued.Abandoned);
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
        uint attempt = 1) : IQueuedRequest
    {
        public bool Completed { get; private set; }

        public bool Abandoned { get; private set; }
        public RequestMetadata Metadata { get; } = RequestMetadata.Create();
        public RequestInvocation Invocation => new QueueInvocation("queue://test/work/item", Attempt);

        public IRequest Request { get; } = throwOnDispatch ? new ThrowingChangeValue(0) : request;

        public string? ActorToken { get; } = actorToken;

        public uint Attempt => attempt;

        public ValueTask CompleteAsync(CancellationToken ct = default)
        {
            Completed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask AbandonAsync(CancellationToken ct = default)
        {
            Abandoned = true;
            return ValueTask.CompletedTask;
        }
    }

    sealed class RecordingTerminalHandler(bool throws = false) : IQueuedRequestTerminalHandler
    {
        public List<QueuedRequestFailureContext> Failures { get; } = [];

        public ValueTask HandleAsync(QueuedRequestFailureContext context, CancellationToken ct = default)
        {
            Failures.Add(context);
            return throws
                ? ValueTask.FromException(new InvalidOperationException("terminal failed"))
                : ValueTask.CompletedTask;
        }
    }

    sealed record ThrowingChangeValue(int Value) : IRequest;

    internal sealed record InvalidChangeValue(int Value) : IRequest;

    internal sealed class InvalidChangeValueHandler : IRequestHandler<InvalidChangeValue>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<InvalidChangeValue> context, CancellationToken ct) =>
            ValueTask.FromResult(Result.Failure(new RequestError(RequestErrorKind.Validation, "Value is invalid.")));
    }
}
