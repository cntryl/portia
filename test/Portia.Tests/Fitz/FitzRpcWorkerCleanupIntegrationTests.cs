using Cntryl.Fitz;

namespace Cntryl.Portia;

/// <summary>Characterizes native backpressure while terminal-response callback cleanup is still running.</summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
[Trait("Category", "BrokerIntegration")]
public sealed class FitzRpcWorkerCleanupIntegrationTests(FitzBrokerFixture broker)
{
    /// <summary>Cleanup can reject a later call without dispatch, while greater declared capacity admits it.</summary>
    /// <param name="portia">Whether Portia owns the callback and asynchronous application scope.</param>
    /// <param name="maxConcurrency">Native per-worker execution capacity; two is the positive control.</param>
    [Theory]
    [InlineData(false, 1U)]
    [InlineData(false, 2U)]
    [InlineData(true, 1U)]
    public async Task ShouldExposeDeclaredBackpressureDuringWorkerCleanup(bool portia, uint maxConcurrency)
    {
        // Arrange
        var endpoint = new Uri(Environment.GetEnvironmentVariable("FITZ_TEST_ENDPOINT")
            ?? "ws://127.0.0.1:4090/ws");
        // Keep domain dispatch parallel enough to expose the independent per-worker limit on any CPU count.
        await using var workerClient = new Client(new ClientConfig(endpoint,
            AsyncHandlers: new AsyncHandlerOptions(MaxConcurrency: 4)));
        await workerClient.ConnectWhenReadyAsync(new ConnectWhenReadyOptions(TimeSpan.FromSeconds(15)));
        await using var callerClient = await broker.CreateClientAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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

            Assert.Equal(portia ? 7 : 42, await call().WaitAsync(TimeSpan.FromSeconds(5)));
            await cleanup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = call();
            if (maxConcurrency == 1)
            {
                // Normal terminal frames release broker credit before local callback cleanup.
                // The SDK's still-occupied slot rejects admission with a known-not-applied result.
                var error = await Assert.ThrowsAsync<RpcException>(() => second.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal("BUSY", error.Code);
                Assert.Equal(FitzErrorCodes.RpcBackpressure, error.DomainCode);
                Assert.Equal((byte)1, error.Status);
                Assert.Equal((byte)5, error.ErrorClass);
                Assert.Equal((byte)0, error.ErrorEffect);
                Assert.Equal(1, cleanup.Invocations);
            }
            else
            {
                Assert.Equal(42, await second.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(2, cleanup.Invocations);
            }
            Assert.False(cleanup.Release.Task.IsCompleted);
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
