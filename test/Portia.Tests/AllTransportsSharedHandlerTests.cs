namespace Cntryl.Portia;

/// <summary>
/// Proves the framework's central claim directly: one handler, unmodified, runs identically
/// whether reached by direct in-process dispatch, a queue, RPC, or a live delivery (the shape
/// shared by Fitz notice fanout and a fired Fitz schedule entry). <see cref="UniversalAction" />
/// and <see cref="UniversalActionHandler" /> are dispatched through all four transport runners in
/// this one test — not four different handlers that merely look alike.
/// </summary>
public sealed class AllTransportsSharedHandlerTests
{
    /// <summary>
    /// Verifies that the same handler instance is invoked by direct dispatch, a queue, RPC, and a
    /// live delivery — four separate transport paths, one shared implementation.
    /// </summary>
    [Fact]
    public async Task ShouldInvokeSameHandlerAcrossDirectQueueRpcAndLiveTransports()
    {
        var handler = new UniversalActionHandler();
        var bus = TestRequestBus.Create(universalActionHandler: handler);

        // Direct in-process dispatch — no transport at all.
        var directResult = await bus.SendAsync(new UniversalAction(1), RequestActor.System);
        Assert.True(directResult.IsSuccess);
        Assert.Equal([1], handler.HandledValues);

        // Queue.
        var queueConsumer = new FakeQueueConsumer([new FakeQueuedItem(new UniversalAction(2))]);
        var queueRunner = new QueueRunner(queueConsumer, bus, new AlwaysValidActorValidator());
        await queueRunner.RunAsync();
        Assert.Equal([1, 2], handler.HandledValues);

        // RPC — a real send through FitzRemoteRequestSender, over FitzRpcRequestServer, into the
        // same bus, and back.
        var rpc = new InMemoryRpcClient();
        var serializer = new JsonRequestSerializer();
        var server = new FitzRpcRequestServer(rpc, serializer, bus, new AlwaysValidActorValidator());
        _ = await server.RegisterAsync<UniversalAction>();
        var sender = new FitzRemoteRequestSender(rpc, serializer);
        var rpcResult = await sender.SendAsync(new UniversalAction(3), new RequestRouteValues(), actorToken: null);
        Assert.True(rpcResult.IsSuccess);
        Assert.Equal([1, 2, 3], handler.HandledValues);

        // Live delivery — the shape shared by Fitz notice fanout and a fired schedule entry.
        var liveConsumer = new FakeLiveRequestConsumer([new LiveRequest(new UniversalAction(4), ActorToken: null)]);
        var liveRunner = new LiveRequestRunner(liveConsumer, bus, new AlwaysValidActorValidator());
        await liveRunner.RunAsync();
        Assert.Equal([1, 2, 3, 4], handler.HandledValues);
    }

    sealed class AlwaysValidActorValidator : IRequestActorValidator
    {
        public ValueTask<Result<System.Security.Claims.ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default) =>
            ValueTask.FromResult(Result<System.Security.Claims.ClaimsPrincipal>.Success(RequestActor.System));
    }

    sealed class FakeQueueConsumer(IReadOnlyList<IQueuedRequest> items) : IRequestQueueConsumer
    {
        public async IAsyncEnumerable<IQueuedRequest> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return item;
            }
        }
    }

    sealed class FakeQueuedItem(IRequest request) : IQueuedRequest
    {
        public IRequest Request { get; } = request;

        public string? ActorToken => null;

        public uint Attempt => 1;

        public ValueTask CompleteAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask AbandonAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    sealed class FakeLiveRequestConsumer(IReadOnlyList<LiveRequest> items) : ILiveRequestConsumer
    {
        public async IAsyncEnumerable<LiveRequest> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return item;
            }
        }
    }
}

// Opts into every transport marker at once — the one thing that's actually different per
// transport is which marker interfaces a request declares, never the handler.
[RequestRoute(realm: "test", area: "shared", resource: "action", operation: "run")]
sealed record UniversalAction(int Value) : IRequest, ICallable, IQueuable, INotifiable, ISchedulable;

sealed class UniversalActionHandler : IRequestHandler<UniversalAction>
{
    readonly List<int> _handledValues = [];

    public IReadOnlyList<int> HandledValues => _handledValues;

    public ValueTask<Result> HandleAsync(IRequestContext<UniversalAction> context, CancellationToken ct)
    {
        _handledValues.Add(context.Request.Value);
        return ValueTask.FromResult(Result.Success);
    }
}
