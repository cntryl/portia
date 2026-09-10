using System.Runtime.CompilerServices;
using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
///     Proves the framework's central claim directly: one handler, unmodified, runs identically
///     whether reached by direct in-process dispatch, a queue, RPC, or a notification (the shape
///     shared by Fitz notice fanout and a fired Fitz schedule entry). <see cref="UniversalAction" />
///     and <see cref="UniversalActionHandler" /> are dispatched through all four transport runners in
///     this one test — not four different handlers that merely look alike.
/// </summary>
public sealed class AllTransportsSharedHandlerTests
{
    /// <summary>
    ///     Verifies that the same handler instance is invoked by direct dispatch, a queue, RPC, and a
    ///     notification — four separate transport paths, one shared implementation.
    /// </summary>
    [Fact]
    public async Task ShouldInvokeSameHandlerAcrossDirectQueueRpcAndNotificationTransports()
    {
        var handler = new UniversalActionHandler();
        using var busHost = TestRequestBus.Create(universalActionHandler: handler);
        var bus = busHost.Bus;

        // Direct in-process dispatch — no transport at all.
        var directResult = await bus.SendAsync(new UniversalAction(1), RequestActor.System);
        Assert.True(directResult.IsSuccess);
        Assert.Equal([1], handler.HandledValues);

        // Queue.
        var queueConsumer = new FakeQueueConsumer([new FakeQueuedItem(new UniversalAction(2))]);
        var queueRunner = new QueueRunner(queueConsumer,
            RequestDeliveryScopes.FixedQueue(bus, new AlwaysValidActorValidator()));
        await queueRunner.RunAsync();
        Assert.Equal([1, 2], handler.HandledValues);

        // RPC — a real send through FitzRemoteRequestSender, over FitzRpcRequestServer, into the
        // same bus, and back.
        var rpc = new InMemoryRpcClient();
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var server = new FitzRpcRequestServer(rpc, busHost.ScopeFactory);
        _ = await server.RegisterAsync<UniversalAction>();
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer);
        var rpcResult = await sender.SendAsync(new UniversalAction(3), new RequestRouteValues(), null);
        Assert.True(rpcResult.IsSuccess);
        Assert.Equal([1, 2, 3], handler.HandledValues);

        // Notification — the shape shared by Fitz notice fanout and a fired schedule entry.
        var notificationConsumer = new FakeRequestNotificationConsumer(
        [
            new RequestNotification(new UniversalAction(4), null, RequestMetadata.Create(),
                new NoticeInvocation("notice://test/work/item"))
        ]);
        var notificationRunner = new RequestNotificationRunner(
            notificationConsumer, RequestDeliveryScopes.Fixed(bus, new AlwaysValidActorValidator()));
        await notificationRunner.RunAsync();
        Assert.Equal([1, 2, 3, 4], handler.HandledValues);
    }

    sealed class AlwaysValidActorValidator : IRequestActorValidator
    {
        public ValueTask<Result<ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default) =>
            ValueTask.FromResult(Result<ClaimsPrincipal>.Success(RequestActor.System));
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

    sealed class FakeQueuedItem(IRequest request) : IQueuedRequest
    {
        public RequestMetadata Metadata { get; } = RequestMetadata.Create();
        public RequestInvocation Invocation => new QueueInvocation("queue://test/work/item", Attempt);
        public IRequest Request { get; } = request;

        public string? ActorToken => null;

        public uint Attempt => 1;

        public ValueTask CompleteAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask AbandonAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    sealed class FakeRequestNotificationConsumer(IReadOnlyList<RequestNotification> items)
        : IRequestNotificationConsumer
    {
        public async IAsyncEnumerable<RequestNotification> ReadAsync(
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
}

// Opts into every transport marker at once — the one thing that's actually different per
// transport is which marker interfaces a request declares, never the handler.
