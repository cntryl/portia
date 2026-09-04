using Cntryl.Fitz.Abstractions.Domains.Lease;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
/// Verifies that <c>Portia.DependencyInjection</c>'s (and, for
/// <see cref="FleetPartitionRunner" />, <c>Portia.Fitz</c>'s) hosting extensions actually start
/// and stop the runner they wire up — not just that DI resolves it, but that a real
/// <see cref="IHostedService" /> calling <c>StartAsync</c>/<c>StopAsync</c> makes the runner do
/// its job and shut down cleanly. Before this, nothing in Portia called any runner's
/// <c>RunAsync</c> automatically at all; an app had to write this glue itself.
/// </summary>
public sealed class PortiaHostingServiceCollectionExtensionsTests
{
    /// <summary>
    /// Preserves the original two-parameter CLR extension method for existing compiled callers.
    /// </summary>
    [Fact]
    public void ShouldRetainOriginalReactorRegistrationOverload()
    {
        var overload = typeof(PortiaHostingServiceCollectionExtensions)
            .GetMethods()
            .SingleOrDefault(candidate =>
            {
                if (candidate.Name != nameof(PortiaHostingServiceCollectionExtensions.AddPortiaReactorRunner)
                    || !candidate.IsGenericMethodDefinition)
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType == typeof(IServiceCollection)
                    && parameters[1].ParameterType == typeof(TimeSpan?);
            });

        Assert.NotNull(overload);
    }

    /// <summary>
    /// Rejects an invalid reactor batch size during registration rather than deferring the error
    /// until the hosted service is resolved and enters its retry loop.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShouldRejectNonPositiveReactorBatchSizeDuringRegistration(int maxBatchSize)
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddPortiaReactorRunner<TestReactor>(maxBatchSize: maxBatchSize));

        Assert.Equal(nameof(maxBatchSize), exception.ParamName);
    }

    /// <summary>
    /// Verifies that <c>AddPortiaQueueRunner</c> actually dispatches a queued request once the
    /// hosted service starts.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchQueuedRequestWhenHostedServiceStarts()
    {
        var handler = new ChangeValueHandler();
        var services = new ServiceCollection();
        _ = services.AddSingleton<IRequestBus>(TestRequestBus.Create(changeValueHandler: handler));
        _ = services.AddSingleton<IRequestQueueConsumer>(new HostingFakeQueueConsumer([new ChangeValue(42)]));
        _ = services.AddSingleton<IRequestActorValidator>(new TestRequestActorValidator());
        _ = services.AddPortiaQueueRunner();
        using var provider = services.BuildServiceProvider();

        var hostedService = Assert.Single(provider.GetServices<IHostedService>());
        await hostedService.StartAsync(default);
        await WaitUntilAsync(() => handler.LastValue == 42);
        await hostedService.StopAsync(default);

        Assert.Equal(42, handler.LastValue);
    }

    /// <summary>
    /// Verifies that <c>AddPortiaMultiTenantRunner</c> starts a tenant's callback when the
    /// hosted service starts, with the app's own <see cref="IServiceProvider" /> available to it,
    /// and stops it cleanly on shutdown.
    /// </summary>
    [Fact]
    public async Task ShouldRunTenantCallbacksWhenHostedServiceStarts()
    {
        var started = new List<TenantId>();
        var stopped = new List<TenantId>();
        var services = new ServiceCollection();
        _ = services.AddSingleton<ITenantDirectory>(new HostingFakeTenantDirectory([new TenantId("acme")]));
        _ = services.AddPortiaMultiTenantRunner(
            onTenantStarted: async (_, tenantId, ct) =>
            {
                started.Add(tenantId);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    // Expected — the host is shutting this tenant's work down.
                }
            },
            onTenantStopped: (_, tenantId, _) =>
            {
                stopped.Add(tenantId);
                return Task.CompletedTask;
            });
        using var provider = services.BuildServiceProvider();

        var hostedService = Assert.Single(provider.GetServices<IHostedService>());
        await hostedService.StartAsync(default);
        await WaitUntilAsync(() => started.Count == 1);
        await hostedService.StopAsync(default);

        Assert.Equal([new TenantId("acme")], started);
        Assert.Equal([new TenantId("acme")], stopped);
    }

    /// <summary>
    /// Verifies that <c>AddPortiaProjectorRunner</c> runs a pass and persists a checkpoint
    /// through the app's own <see cref="IProjectionCheckpointStore" /> when the hosted service
    /// starts.
    /// </summary>
    [Fact]
    public async Task ShouldRunProjectorPassAndSaveCheckpointWhenHostedServiceStarts()
    {
        var id = Uuid.CreateVersion7();
        var stream = new EventStreamAddress("test", "projectors", id.ToString());
        var store = new InMemoryEventStore();
        await store.AppendAsync(stream, 0, [Committed(new ValueChanged(42), id, 1)]);
        var target = new RecordingProjectionTarget();
        var checkpointStore = new InMemoryProjectionCheckpointStore();

        var services = new ServiceCollection();
        _ = services.AddSingleton<IDomainEventReader>(store);
        _ = services.AddSingleton<Projector<TestProjection>>(new TestProjector(target));
        _ = services.AddSingleton<IProjectionCheckpointStore>(checkpointStore);
        _ = services.AddPortiaProjectorRunner<TestProjection>(pollInterval: TimeSpan.FromMilliseconds(20));
        using var provider = services.BuildServiceProvider();

        var hostedService = Assert.Single(provider.GetServices<IHostedService>());
        await hostedService.StartAsync(default);
        await WaitUntilAsync(() => target.Projection.Value == 42);
        await hostedService.StopAsync(default);

        Assert.Equal(42, target.Projection.Value);
        Assert.Equal(1UL, (await checkpointStore.LoadAsync("test-projector")).NextOffset);
    }

    /// <summary>
    /// Verifies that <c>Portia.Fitz</c>'s <c>AddPortiaFleetPartitionRunner</c> acquires its
    /// partition and runs the caller's callback when the hosted service starts.
    /// </summary>
    [Fact]
    public async Task ShouldAcquirePartitionWhenFleetHostedServiceStarts()
    {
        var acquired = new List<string>();
        var services = new ServiceCollection();
        _ = services.AddSingleton<ILeaseClient>(new InMemoryLeaseClient());
        _ = services.AddPortiaFleetPartitionRunner(
            ["lease://portia/fleet/partition-a"],
            onPartitionAcquired: async (_, partition, _, ct) =>
            {
                acquired.Add(partition);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    // Expected — the host is shutting this partition's work down.
                }
            },
            leaseTtl: TimeSpan.FromSeconds(30));
        using var provider = services.BuildServiceProvider();

        var hostedService = Assert.Single(provider.GetServices<IHostedService>());
        await hostedService.StartAsync(default);
        await WaitUntilAsync(() => acquired.Count == 1);
        await hostedService.StopAsync(default);

        Assert.Equal(["lease://portia/fleet/partition-a"], acquired);
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);

        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(condition());
    }

    static T Committed<T>(T ev, Uuid aggregateId, ulong aggregateVersion)
        where T : DomainEvent
    {
        ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion7(), aggregateId, aggregateVersion, DateTimeOffset.UtcNow));
        return ev;
    }

    sealed class HostingFakeQueueConsumer(IReadOnlyList<IRequest> items) : IRequestQueueConsumer
    {
        public async IAsyncEnumerable<IQueuedRequest> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return new HostingFakeQueuedRequest(item);
            }

            // Blocks after the last item instead of completing, matching a real queue consumer
            // (which never runs out — it waits for more work) so the hosted service's RunAsync
            // loop stays alive until the test explicitly stops it, not because it ran dry.
            await using (ct.Register(static () => { }))
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
    }

    sealed class HostingFakeQueuedRequest(IRequest request) : IQueuedRequest
    {
        public IRequest Request { get; } = request;

        public string? ActorToken => null;

        public uint Attempt => 1;

        public ValueTask CompleteAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask AbandonAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    sealed class HostingFakeTenantDirectory(IReadOnlyList<TenantId> initial) : ITenantDirectory
    {
        public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var tenantId in initial)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return tenantId;
            }
        }

        public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (ct.Register(() => tcs.TrySetResult()))
                await tcs.Task.ConfigureAwait(false);

            yield break;
        }
    }
}
