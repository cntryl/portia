using System.Text.Json;
using Cntryl.Fitz;

namespace Cntryl.Portia;

/// <summary>Checks addressing, endpoint selection, and exact request binding for processor status RPC.</summary>
public sealed class ProcessorStatusRpcTests
{
    /// <summary>Shared status contracts do not activate a general RPC listener or its startup requirements.</summary>
    [Fact]
    public void ShouldKeepProcessorStatusSeparateFromGeneralRequestWorkerSelection()
    {
        var application = new ServiceCollection().AddPortia();
        ProcessorStatusRegistration.Add(application);
        var fitz = new PortiaFitzBuilder(application);

        Assert.Empty(fitz.Workers);
    }

    /// <summary>ApplicationName's mapped fleet and an explicit status namespace use identical addresses.</summary>
    [Fact]
    public void ShouldShareStatusNamespaceWithApplicationFleetConfiguration()
    {
        var fleet = new PortiaFitzBuilder(new ServiceCollection().AddPortia());
        _ = fleet.UseFleet(new FleetRunOptions { MembershipSelector = "lease://accounts/portia-members/*" });
        var explicitNamespace = new PortiaFitzBuilder(new ServiceCollection().AddPortia());
        _ = explicitNamespace.UseProcessorStatusRpc("accounts");

        Assert.Equal(fleet.ProcessorStatusNamespace, explicitNamespace.ProcessorStatusNamespace);
        Assert.Equal(FitzProcessorStatusRoutes.Route(fleet.ProcessorStatusNamespace, new WorkloadIdentity("balances")),
            FitzProcessorStatusRoutes.Route(explicitNamespace.ProcessorStatusNamespace, new WorkloadIdentity("balances")));
    }

    /// <summary>Arbitrary stable names produce exact, distinct application and tenant routes.</summary>
    [Fact]
    public void ShouldAddressExactApplicationProcessorAndTenantWithoutRestrictingComponentNames()
    {
        var workload = new WorkloadIdentity("account balances / \u03b1", new TenantId("tenant-a"));
        var route = FitzProcessorStatusRoutes.Route("application-a", workload);

        Assert.Equal(route, FitzProcessorStatusRoutes.Route("application-a", workload));
        Assert.NotEqual(route, FitzProcessorStatusRoutes.Route("application-b", workload));
        Assert.NotEqual(route, FitzProcessorStatusRoutes.Route("application-a",
            new WorkloadIdentity(workload.Name, new TenantId("tenant-b"))));
        Assert.NotEqual(route, FitzProcessorStatusRoutes.Route("application-a", new WorkloadIdentity(workload.Name)));
        Assert.DoesNotContain('*', route);
        Assert.DoesNotContain(workload.Name, route, StringComparison.Ordinal);
    }

    /// <summary>The built-in status contract never creates a broad callable listener.</summary>
    [Fact]
    public async Task ShouldLeaveStatusRpcDormantOutsideWorkloadOwnership()
    {
        var services = new ServiceCollection();
        _ = services.AddPortia();
        ProcessorStatusRegistration.Add(services.AddPortia());
        await using var provider = services.BuildServiceProvider();
        var rpc = new InMemoryRpcClient();
        var catalog = new RequestTransportCatalog(provider.GetServices<RequestTransportRegistration>());
        var serializer = new JsonRequestSerializer(provider.GetServices<RequestTransportRegistration>(),
            provider.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json));
        var server = new FitzRpcRequestServer(rpc, provider.GetRequiredService<IServiceScopeFactory>(), catalog);
        await using var registrations = await server.RegisterRequestsAsync();
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer, catalog);
        var client = new FitzProcessorStatusClient(sender, () => "application");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.GetAsync("balances"));
    }

    /// <summary>A concrete endpoint rejects a payload naming a different processor or tenant before dispatch.</summary>
    [Theory]
    [InlineData("different-processor", "tenant-a")]
    [InlineData("balances", "tenant-b")]
    [InlineData("balances", null)]
    public async Task ShouldRejectRequestThatDoesNotBelongToConcreteEndpoint(string processor, string? tenant)
    {
        var services = new ServiceCollection();
        ProcessorStatusRegistration.Add(services.AddPortia());
        // A mismatched body must be rejected by the endpoint before authorizing or executing
        // the request against any component's checkpoint.
        _ = services.AddSingleton<IRequestActorValidator>(new TestRequestActorValidator());
        _ = services.AddSingleton(static provider => new JsonRequestSerializer(
            provider.GetServices<RequestTransportRegistration>(),
            provider.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json)));
        _ = services.AddSingleton<IRequestDeserializer>(static provider => provider.GetRequiredService<JsonRequestSerializer>());
        _ = services.AddSingleton<IRequestOutcomeSerializer>(static provider => provider.GetRequiredService<JsonRequestSerializer>());
        await using var provider = services.BuildServiceProvider();
        var rpc = new InMemoryRpcClient();
        var catalog = new RequestTransportCatalog(provider.GetServices<RequestTransportRegistration>());
        var serializer = provider.GetRequiredService<JsonRequestSerializer>();
        var identity = new WorkloadIdentity("balances", new TenantId("tenant-a"));
        var route = FitzProcessorStatusRoutes.Route("application", identity);
        var server = new FitzRpcRequestServer(rpc, provider.GetRequiredService<IServiceScopeFactory>(), catalog);
        await using var registration = await server.RegisterAsync<GetProcessorStatus, ProcessorStatus>(route,
            (request, received) => received == route && request.Processor == identity.Name
                && request.Tenant == identity.Tenant?.Value, default);
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer, catalog);

        var result = await sender.SendAsync<GetProcessorStatus, ProcessorStatus>(
            new GetProcessorStatus(processor, tenant), FitzProcessorStatusRoutes.Values("application", identity), "actor");

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Validation, result.Error.Kind);
    }

    /// <summary>A retained native subscription cannot dispatch status after ownership is revoked.</summary>
    /// <param name="cancelOwnership">Whether ownership cancellation precedes endpoint disposal.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectStaleCallbackWhenNativeWithdrawalFails(bool cancelOwnership)
    {
        var services = new ServiceCollection();
        var handler = new CountingStatusHandler();
        _ = services.AddSingleton(handler);
        _ = services.AddSingleton<IRequestActorValidator>(new TestRequestActorValidator());
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<GetProcessorStatus, CountingStatusHandler, ProcessorStatus>());
        _ = application.AddGeneratedRequest(new RequestTransportRegistration(typeof(GetProcessorStatus),
            [RequestTransportId.Callable], new RequestRouteAttribute("*", "*", "*", "status"),
            new DiscriminatorAttribute("portia.processor_status", 1),
            static (registrar, ct) => registrar.RegisterAsync<GetProcessorStatus, ProcessorStatus>(ct),
            typeof(ProcessorStatus)));
        _ = application.AddGeneratedJsonContext(static options => new ProcessorStatusJsonContext(options));
        _ = services.AddSingleton(static provider => new JsonRequestSerializer(
            provider.GetServices<RequestTransportRegistration>(),
            provider.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json)));
        _ = services.AddSingleton<IRequestDeserializer>(static provider => provider.GetRequiredService<JsonRequestSerializer>());
        _ = services.AddSingleton<IRequestOutcomeSerializer>(static provider => provider.GetRequiredService<JsonRequestSerializer>());
        await using var provider = services.BuildServiceProvider();
        await using var rpc = new RetainedRpcClient();
        var catalog = new RequestTransportCatalog(provider.GetServices<RequestTransportRegistration>());
        var serializer = provider.GetRequiredService<JsonRequestSerializer>();
        var identity = new WorkloadIdentity("balances");
        var route = FitzProcessorStatusRoutes.Route("application", identity);
        using var ownership = new CancellationTokenSource();
        await using var owned = new FitzProcessorStatusRpcRegistration(identity, route, ownership.Token);
        var server = new FitzRpcRequestServer(rpc, provider.GetRequiredService<IServiceScopeFactory>(), catalog);
        owned.Attach(await server.RegisterAsync<GetProcessorStatus, ProcessorStatus>(route, owned.Accepts, default));
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer, catalog);
        var client = new FitzProcessorStatusClient(sender, () => "application");
        Assert.True((await client.GetAsync(identity.Name, actorToken: "actor")).IsSuccess);
        Assert.Equal(1, handler.Calls);

        if (cancelOwnership)
        {
            ownership.Cancel();
            var cancelledOwner = await client.GetAsync(identity.Name, actorToken: "actor");
            Assert.False(cancelledOwner.IsSuccess);
            Assert.Equal(RequestErrorKind.Validation, cancelledOwner.Error.Kind);
        }
        await owned.DisposeAsync();
        Assert.Equal(1, rpc.FailedWithdrawals);

        var disposedOwner = await client.GetAsync(identity.Name, actorToken: "actor");

        Assert.False(disposedOwner.IsSuccess);
        Assert.Equal(RequestErrorKind.Validation, disposedOwner.Error.Kind);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>A failed native withdrawal is retried before reacquiring the same component endpoint.</summary>
    [Fact]
    public async Task ShouldReacquireEndpointAfterFailedNativeWithdrawal()
    {
        var clock = new ManualTestClock();
        var services = new ServiceCollection();
        var handler = new CountingStatusHandler();
        _ = services.AddSingleton(handler);
        _ = services.AddSingleton<IRequestActorValidator>(new TestRequestActorValidator());
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<GetProcessorStatus, CountingStatusHandler, ProcessorStatus>());
        _ = application.AddGeneratedRequest(new RequestTransportRegistration(typeof(GetProcessorStatus),
            [RequestTransportId.Callable], new RequestRouteAttribute("*", "*", "*", "status"),
            new DiscriminatorAttribute("portia.processor_status", 1),
            static (registrar, ct) => registrar.RegisterAsync<GetProcessorStatus, ProcessorStatus>(ct),
            typeof(ProcessorStatus)));
        _ = application.AddGeneratedJsonContext(static options => new ProcessorStatusJsonContext(options));
        _ = services.AddSingleton(static provider => new JsonRequestSerializer(
            provider.GetServices<RequestTransportRegistration>(),
            provider.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json)));
        _ = services.AddSingleton<IRequestDeserializer>(static provider => provider.GetRequiredService<JsonRequestSerializer>());
        _ = services.AddSingleton<IRequestOutcomeSerializer>(static provider => provider.GetRequiredService<JsonRequestSerializer>());
        await using var provider = services.BuildServiceProvider();
        await using var rpc = new RetainedRpcClient();
        var catalog = new RequestTransportCatalog(provider.GetServices<RequestTransportRegistration>());
        var serializer = provider.GetRequiredService<JsonRequestSerializer>();
        var identity = new WorkloadIdentity("balances");
        await using var registrar = new FitzProcessorStatusRpcRegistrar(rpc, provider.GetRequiredService<IServiceScopeFactory>(),
            catalog, () => "application", timeProvider: clock);
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer, catalog);
        var client = new FitzProcessorStatusClient(sender, () => "application");
        var first = await registrar.RegisterAsync(identity);
        Assert.True((await client.GetAsync(identity.Name, actorToken: "actor")).IsSuccess);
        await first.DisposeAsync();
        Assert.Equal(1, rpc.FailedWithdrawals);
        rpc.AllowWithdrawal = true;

        await using var second = await registrar.RegisterAsync(identity);
        var resumed = await client.GetAsync(identity.Name, actorToken: "actor");

        Assert.True(resumed.IsSuccess);
        Assert.Equal(2, handler.Calls);
    }

    /// <summary>Failed withdrawal retries without reacquisition so an inactive listener cannot keep competing.</summary>
    [Fact]
    public async Task ShouldWithdrawRetainedEndpointWithoutReacquiringWorkload()
    {
        var (services, handler) = CountingStatusServices();
        await using var provider = services.BuildServiceProvider();
        await using var rpc = new RetainedRpcClient { AllowWithdrawalAfterFailure = true };
        var catalog = new RequestTransportCatalog(provider.GetServices<RequestTransportRegistration>());
        var serializer = provider.GetRequiredService<JsonRequestSerializer>();
        var identity = new WorkloadIdentity("balances");
        await using var registrar = new FitzProcessorStatusRpcRegistrar(rpc,
            provider.GetRequiredService<IServiceScopeFactory>(), catalog, () => "application");
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer, catalog);
        var client = new FitzProcessorStatusClient(sender, () => "application");
        var endpoint = await registrar.RegisterAsync(identity);
        Assert.True((await client.GetAsync(identity.Name, actorToken: "actor")).IsSuccess);

        await endpoint.DisposeAsync();
        Assert.Equal(1, rpc.FailedWithdrawals);
        await rpc.Withdrawn.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, rpc.ActivePatterns);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GetAsync(identity.Name, actorToken: "actor"));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Shutdown shares one withdrawal deadline instead of waiting once per retained endpoint.</summary>
    [Fact]
    public async Task ShouldShareOneFinalWithdrawalBudgetAcrossRetainedEndpoints()
    {
        var (services, _) = CountingStatusServices();
        await using var provider = services.BuildServiceProvider();
        var clock = new ManualTestClock();
        var rpc = new BlockedWithdrawalRpcClient();
        var catalog = new RequestTransportCatalog(provider.GetServices<RequestTransportRegistration>());
        await using var registrar = new FitzProcessorStatusRpcRegistrar(rpc,
            provider.GetRequiredService<IServiceScopeFactory>(), catalog, () => "application", timeProvider: clock);
        var first = await registrar.RegisterAsync(new WorkloadIdentity("first"));
        var second = await registrar.RegisterAsync(new WorkloadIdentity("second"));
        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.Equal(2, rpc.InitialFailures);
        rpc.BlockWithdrawals = true;

        var shutdown = registrar.DisposeAsync().AsTask();
        try
        {
            await rpc.FirstWithdrawalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(4));
            Assert.False(shutdown.IsCompleted);
            _ = rpc.FailFirstWithdrawal.TrySetResult();
            await rpc.SecondWithdrawalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The second endpoint started four seconds into the shared five-second budget.
            // Its own attempt deadline would allow another five seconds, so shutdown must
            // instead cancel it when the original budget has only one second left.
            clock.Advance(TimeSpan.FromSeconds(1));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, rpc.BlockingWithdrawals);
            Assert.True(rpc.SecondWithdrawalCancelled);
        }
        finally
        {
            // Release every fixture gate even if the deadline assertion fails, including
            // when checking a mutation that gives the second endpoint a fresh budget.
            _ = rpc.FailFirstWithdrawal.TrySetResult();
            _ = rpc.ReleaseSecondWithdrawal.TrySetResult();
            clock.Advance(TimeSpan.FromDays(1));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    static (ServiceCollection Services, CountingStatusHandler Handler) CountingStatusServices()
    {
        var services = new ServiceCollection();
        var handler = new CountingStatusHandler();
        _ = services.AddSingleton(handler);
        _ = services.AddSingleton<IRequestActorValidator>(new TestRequestActorValidator());
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<GetProcessorStatus, CountingStatusHandler, ProcessorStatus>());
        _ = application.AddGeneratedRequest(new RequestTransportRegistration(typeof(GetProcessorStatus),
            [RequestTransportId.Callable], new RequestRouteAttribute("*", "*", "*", "status"),
            new DiscriminatorAttribute("portia.processor_status", 1),
            static (registrar, ct) => registrar.RegisterAsync<GetProcessorStatus, ProcessorStatus>(ct),
            typeof(ProcessorStatus)));
        _ = application.AddGeneratedJsonContext(static options => new ProcessorStatusJsonContext(options));
        _ = services.AddSingleton(static provider => new JsonRequestSerializer(
            provider.GetServices<RequestTransportRegistration>(),
            provider.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json)));
        _ = services.AddSingleton<IRequestDeserializer>(static provider => provider.GetRequiredService<JsonRequestSerializer>());
        _ = services.AddSingleton<IRequestOutcomeSerializer>(static provider => provider.GetRequiredService<JsonRequestSerializer>());
        return (services, handler);
    }

    internal sealed class CountingStatusHandler : IRequestHandler<GetProcessorStatus, ProcessorStatus>
    {
        internal int Calls { get; private set; }

        public ValueTask<Result<ProcessorStatus>> HandleAsync(IRequestContext<GetProcessorStatus> context,
            CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(Result<ProcessorStatus>.Success(new ProcessorStatus(context.Request.Processor,
                "projector", context.Request.Tenant, "stream://global/events/*", null,
                new EventStreamProgress(EventCursor.Start, EventCursor.Start, 0, true, DateTimeOffset.UtcNow), null)));
        }
    }

    sealed class BlockedWithdrawalRpcClient : IRpcClient
    {
        int _initialFailures;
        int _blockingWithdrawals;
        int _secondWithdrawalCancelled;
        internal bool BlockWithdrawals { get; set; }
        internal int InitialFailures => Volatile.Read(ref _initialFailures);
        internal int BlockingWithdrawals => Volatile.Read(ref _blockingWithdrawals);
        internal bool SecondWithdrawalCancelled => Volatile.Read(ref _secondWithdrawalCancelled) != 0;
        internal TaskCompletionSource FirstWithdrawalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FailFirstWithdrawal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondWithdrawalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSecondWithdrawal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

#pragma warning disable RS0026
        public RpcCall CallAsync(string route, ReadOnlyMemory<byte> body, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public RpcCall CallAsync(string route, ReadOnlyMemory<byte> body, TimeSpan? timeout,
            CancellationToken ct = default) => throw new NotSupportedException();
#pragma warning restore RS0026

        public Task<RpcWorkerRegistration> RegisterWorkerAsync(string pattern,
            Func<RpcRequest, IRpcResponseWriter, CancellationToken, ValueTask> handler,
            RpcWorkerOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new RpcWorkerRegistration(pattern, async token =>
            {
                if (!BlockWithdrawals)
                {
                    _ = Interlocked.Increment(ref _initialFailures);
                    throw new IOException("Broker unavailable.");
                }
                if (Interlocked.Increment(ref _blockingWithdrawals) == 1)
                {
                    _ = FirstWithdrawalStarted.TrySetResult();
                    await FailFirstWithdrawal.Task.WaitAsync(token);
                    throw new IOException("First final withdrawal failed.");
                }
                _ = SecondWithdrawalStarted.TrySetResult();
                try
                {
                    await ReleaseSecondWithdrawal.Task.WaitAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Volatile.Write(ref _secondWithdrawalCancelled, 1);
                    throw;
                }
            }));
    }

    sealed class RetainedRpcClient : IRpcClient, IAsyncDisposable
    {
        readonly InMemoryRpcClient _inner = new();
        readonly List<RpcWorkerRegistration> _registrations = [];
        readonly HashSet<string> _patterns = new(StringComparer.Ordinal);
        internal int FailedWithdrawals { get; private set; }
        internal bool AllowWithdrawal { get; set; }
        internal bool AllowWithdrawalAfterFailure { get; init; }
        internal int ActivePatterns => _patterns.Count;
        internal TaskCompletionSource Withdrawn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

#pragma warning disable RS0026
        public RpcCall CallAsync(string route, ReadOnlyMemory<byte> body, CancellationToken ct = default)
            => _inner.CallAsync(route, body, ct);

        public RpcCall CallAsync(string route, ReadOnlyMemory<byte> body, TimeSpan? timeout, CancellationToken ct = default)
            => _inner.CallAsync(route, body, timeout, ct);
#pragma warning restore RS0026

        public async Task<RpcWorkerRegistration> RegisterWorkerAsync(string pattern,
            Func<RpcRequest, IRpcResponseWriter, CancellationToken, ValueTask> handler,
            RpcWorkerOptions? options = null, CancellationToken ct = default)
        {
            if (!_patterns.Add(pattern))
                throw new InvalidOperationException("ALREADY_REGISTERED");
            var registration = await _inner.RegisterWorkerAsync(pattern, handler, options, ct);
            _registrations.Add(registration);
            return new RpcWorkerRegistration(pattern, token =>
            {
                if (AllowWithdrawal)
                    return WithdrawAsync(registration, pattern, token);
                FailedWithdrawals++;
                if (AllowWithdrawalAfterFailure)
                    AllowWithdrawal = true;
                return ValueTask.FromException(new IOException("Native withdrawal failed."));
            });
        }

        async ValueTask WithdrawAsync(RpcWorkerRegistration registration, string pattern, CancellationToken ct)
        {
            await registration.UnregisterAsync(ct);
            _ = _patterns.Remove(pattern);
            Withdrawn.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var registration in _registrations)
                await registration.DisposeAsync();
        }
    }
}
