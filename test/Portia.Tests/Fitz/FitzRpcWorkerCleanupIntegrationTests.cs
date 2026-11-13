using Cntryl.Fitz;

namespace Cntryl.Portia;

/// <summary>Characterizes native worker capacity while terminal-response callback cleanup is still running.</summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
[Trait("Category", "BrokerIntegration")]
public sealed class FitzRpcWorkerCleanupIntegrationTests(FitzBrokerFixture broker)
{
    /// <summary>
    ///     Cleanup holds a later call without dispatch until the slot frees, while greater declared capacity
    ///     admits it at once. Fitz rejects with backpressure only once its bounded wait queue is full.
    /// </summary>
    /// <param name="portia">Whether Portia owns the callback and asynchronous application scope.</param>
    /// <param name="maxConcurrency">Native per-worker execution capacity; two is the positive control.</param>
    [Theory]
    [InlineData(false, 1U)]
    [InlineData(false, 2U)]
    [InlineData(true, 1U)]
    public async Task ShouldHoldCallUntilCapacityFreesGivenWorkerCleanupInProgress(bool portia, uint maxConcurrency)
    {
        // Arrange
        var endpoint = new Uri(Environment.GetEnvironmentVariable("FITZ_TEST_ENDPOINT")
            ?? "ws://127.0.0.1:4090/ws");
        // Keep domain dispatch parallel enough to expose the independent per-worker limit on any CPU count.
        await using var workerClient = new FitzClient(new ClientConfig(endpoint,
            AsyncHandlers: new AsyncHandlerOptions(MaxConcurrency: 4)));
        await workerClient.ConnectWhenReadyAsync(new ConnectWhenReadyOptions(TimeSpan.FromSeconds(15)));
        await using var callerClient = await broker.CreateClientAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var bus = TestRequestBus.Create();
        var resource = "worker-cleanup-" + Uuid.CreateVersion4();
        var route = $"rpc://test/cleanup/{resource}/call";
        var cleanup = new CleanupGate();
        RpcWorkerRegistration? registration = null;
        Task<int>? second = null;

        // Act
        Func<Task<int>> call;

        // Assert
        try
        {
            if (portia)
            {
                var descriptor = new RequestTransportRegistration(typeof(RpcGetValue), [RequestTransportId.Callable],
                    new RequestRouteAttribute("test", "cleanup", resource, "call"),
                    new DiscriminatorAttribute("test.rpc.get-value"), resultType: typeof(int));
                var catalog = new RequestTransportCatalog([descriptor]);
                var serializer = new JsonRequestSerializer([descriptor], TestJson.Options());
                var server = new FitzRpcRequestServer(workerClient.Rpc,
                    new GatedScopeFactory(bus.ScopeFactory, cleanup), catalog);
                registration = await server.RegisterAsync<RpcGetValue, int>(route, null, deadline.Token);
                var sender = new FitzRemoteRequestSender(callerClient.Rpc, serializer, serializer, catalog);
                call = async () =>
                {
                    var outcome = await sender.SendAsync<RpcGetValue, int>(new RpcGetValue(),
                        RequestRouteValues.None, null, deadline.Token);
                    Assert.True(outcome.IsSuccess);
                    return outcome.Value;
                };
            }
            else
            {
                registration = await workerClient.Rpc.RegisterWorkerAsync(route, async (_, writer, token) =>
                {
                    var first = cleanup.BeginInvocation() == 1;
                    await writer.SendAsync(new byte[] { 42 }, true, token);
                    if (first)
                        await cleanup.WaitAsync();
                }, new RpcWorkerOptions { MaxConcurrency = maxConcurrency }, deadline.Token);
                call = () => ReadRawAsync(callerClient.Rpc.CallAsync(route, ReadOnlyMemory<byte>.Empty,
                    deadline.Token), deadline.Token);
            }

            var expected = portia ? 7 : 42;
            Assert.Equal(expected, await call().WaitAsync(deadline.Token));
            await cleanup.Entered.Task.WaitAsync(deadline.Token);
            second = call();
            if (maxConcurrency == 1)
            {
                // Normal terminal frames release broker credit before local callback cleanup, so the
                // broker delivers the call while the SDK's only slot is still occupied. The SDK holds it
                // in its bounded wait rather than dispatching or rejecting it. A correct SDK never
                // completes it inside this window, so the window cannot produce a false failure.
                var early = await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token));
                Assert.NotSame(second, early);
                Assert.Equal(1, cleanup.Invocations);
                cleanup.Release.TrySetResult();
                Assert.Equal(expected, await second.WaitAsync(deadline.Token));
                Assert.Equal(2, cleanup.Invocations);
            }
            else
            {
                Assert.Equal(42, await second.WaitAsync(deadline.Token));
                Assert.Equal(2, cleanup.Invocations);
                Assert.False(cleanup.Release.Task.IsCompleted);
            }
        }
        finally
        {
            cleanup.Release.TrySetResult();
            if (second is { } pending)
                _ = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            if (registration is not null)
                await registration.DisposeAsync();
        }
    }

    static async Task<int> ReadRawAsync(RpcCall call, CancellationToken ct)
    {
        var value = -1;
        await foreach (var frame in call.WithCancellation(ct))
            value = frame.Body.Span[0];
        return value;
    }

    sealed class CleanupGate
    {
        int _invocations;

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Invocations => Volatile.Read(ref _invocations);

        internal int BeginInvocation() => Interlocked.Increment(ref _invocations);

        internal Task WaitAsync()
        {
            Entered.TrySetResult();
            return Release.Task;
        }
    }

    sealed class GatedScopeFactory(IServiceScopeFactory inner, CleanupGate cleanup) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new GatedScope(inner.CreateScope(), cleanup,
            cleanup.BeginInvocation() == 1);
    }

    sealed class GatedScope(IServiceScope inner, CleanupGate cleanup, bool first) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => inner.ServiceProvider;

        public void Dispose() => inner.Dispose();

        public async ValueTask DisposeAsync()
        {
            if (first)
                await cleanup.WaitAsync();
            if (inner is IAsyncDisposable asynchronous)
                await asynchronous.DisposeAsync();
            else
                inner.Dispose();
        }
    }
}
