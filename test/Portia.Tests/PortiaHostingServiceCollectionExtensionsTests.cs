using System.Collections.Concurrent;
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
    /// Rejects an invalid reactor batch size during registration rather than deferring the error
    /// until the hosted service is resolved and enters its retry loop.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShouldRejectNonPositiveReactorBatchSizeDuringRegistration(int maxBatchSize)
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => services.AddPortia(p => p.AddReactor<TestReactor>(o =>
        {
            o.Global();
            o.Processing = new ProjectionRunOptions { MaxBatchSize = maxBatchSize };
        })));

        Assert.Equal(nameof(ProjectionRunOptions.MaxBatchSize), exception.ParamName);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(WorkloadRegistration));
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
        using var busHost = TestRequestBus.Create(changeValueHandler: handler);
        _ = services.AddSingleton(busHost.Bus);
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
    /// Verifies that <c>AddPortiaMultiTenantRunner</c> resolves a typed workload in its own scope
    /// and disposes that scope when the tenant stops.
    /// </summary>
    [Fact]
    public async Task ShouldRunScopedTenantWorkloadWhenHostedServiceStarts()
    {
        var state = new HostingWorkloadState();
        var services = new ServiceCollection();
        _ = services.AddSingleton<ITenantDirectory>(new HostingFakeTenantDirectory([new TenantId("acme")]));
        _ = services.AddSingleton(state);
        _ = services.AddScoped<HostingTenantWorkload>();
        _ = services.AddPortiaMultiTenantRunner<HostingTenantWorkload>();
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var hostedService = Assert.Single(provider.GetServices<IHostedService>());
        await hostedService.StartAsync(default);
        await WaitUntilAsync(() => state.StartedTenants.Count == 1);
        await hostedService.StopAsync(default);

        Assert.Equal([new TenantId("acme")], state.StartedTenants);
        Assert.Equal([new TenantId("acme")], state.StoppedTenants);
        _ = Assert.Single(state.WorkloadInstances);
    }

    /// <summary>
    /// Verifies that <c>AddPortiaProjectorRunner</c> runs a pass and resumes from the checkpoint
    /// committed atomically by the projection target.
    /// </summary>
    [Fact]
    public async Task ShouldRunProjectorPassAndSaveCheckpointWhenHostedServiceStarts()
    {
        var id = Uuid.CreateVersion4();
        var stream = new EventStreamAddress("test", "projectors", id.ToString());
        var store = new InMemoryEventStore();
        await store.AppendAsync(stream, 0, [Committed(new ValueChanged(42), id, 1)]);
        var target = new RecordingProjectionTarget();

        var services = new ServiceCollection();
        _ = services.AddSingleton<IDomainEventReader>(store);
        _ = services.AddFrameworkTests();
        _ = services.AddSingleton(new TestProjector(target));
        _ = services.AddPortia(p => p.AddProjector<TestProjector>(o =>
        {
            o.Global();
            o.PollInterval = TimeSpan.FromMilliseconds(20);
        })).AddWorker();
        using var provider = services.BuildServiceProvider();

        var hostedService = Assert.Single(provider.GetServices<IHostedService>());
        await hostedService.StartAsync(default);
        await WaitUntilAsync(() => target.Projection.Value == 42);
        await hostedService.StopAsync(default);

        Assert.Equal(42, target.Projection.Value);
        Assert.Equal(1UL, (await target.LoadCheckpointAsync(new CheckpointIdentity("test-projector", EventStreamPattern.ForPattern("test", "projectors")))).NextOffset);
    }

    /// <summary>
    /// Verifies a late adapter failure after an atomic projection commit reloads the target's
    /// authoritative checkpoint before retrying, so the committed event is not applied twice.
    /// </summary>
    [Fact]
    public async Task ShouldNotReplayCommittedProjectionBatchAfterLateCommitFailure()
    {
        var id = Uuid.CreateVersion4();
        var stream = new EventStreamAddress("test", "projectors", id.ToString());
        var store = new InMemoryEventStore();
        await store.AppendAsync(stream, 0, [Committed(new ValueChanged(42), id, 1)]);
        var target = new LateFaultProjectionTarget();
        var services = new ServiceCollection();
        _ = services.AddSingleton<IDomainEventReader>(store);
        _ = services.AddFrameworkTests();
        _ = services.AddSingleton(new TestProjector(target));
        _ = services.AddPortia(p => p.AddProjector<TestProjector>(o =>
        {
            o.Global();
            o.PollInterval = TimeSpan.FromMilliseconds(10);
        })).AddWorker();
        using var provider = services.BuildServiceProvider();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());

        await hostedService.StartAsync(default);
        await WaitUntilAsync(() => target.CommitAttempts == 1);
        await Task.Delay(50);
        await hostedService.StopAsync(default);

        Assert.Equal(1, target.Projection.HandlerCount);
        Assert.Equal(1UL, (await target.LoadCheckpointAsync(new CheckpointIdentity("test-projector", EventStreamPattern.ForPattern("test", "projectors")))).NextOffset);
    }

    /// <summary>
    /// Verifies a transient failure while reloading the authoritative checkpoint remains inside
    /// the polling loop. The service must retry the load and must not run another projector pass
    /// from its stale in-memory checkpoint while the target remains unavailable.
    /// </summary>
    [Fact]
    public async Task ShouldRetryCheckpointReloadWithoutReplayingFromStaleCheckpoint()
    {
        var id = Uuid.CreateVersion4();
        var stream = new EventStreamAddress("test", "projectors", id.ToString());
        var store = new InMemoryEventStore();
        await store.AppendAsync(stream, 0, [Committed(new ValueChanged(42), id, 1)]);
        var target = new TransientReloadFailureProjectionTarget();
        var services = new ServiceCollection();
        _ = services.AddSingleton<IDomainEventReader>(store);
        _ = services.AddFrameworkTests();
        _ = services.AddSingleton(new TestProjector(target));
        _ = services.AddPortia(p => p.AddProjector<TestProjector>(o =>
        {
            o.Global();
            o.Name = "test-projector";
            o.PollInterval = TimeSpan.FromMilliseconds(10);
        })).AddWorker();
        using var provider = services.BuildServiceProvider();

        var hostedService = (BackgroundService)Assert.Single(provider.GetServices<IHostedService>());
        await hostedService.StartAsync(default);
        var executeTask = hostedService.ExecuteTask
            ?? throw new InvalidOperationException("The projector hosted service did not start.");
        var firstCompletion = await Task.WhenAny(target.CheckpointReloaded, executeTask);
        Assert.Same(target.CheckpointReloaded, firstCompletion);
        await target.CheckpointReloaded;
        await hostedService.StopAsync(default);

        Assert.False(executeTask.IsFaulted);
        Assert.Equal(3, target.LoadAttempts);
        Assert.Equal(1, target.Projection.HandlerCount);
        Assert.Equal(1UL, (await target.LoadCheckpointAsync(new CheckpointIdentity("test-projector", EventStreamPattern.ForPattern("test", "projectors")))).NextOffset);
    }

    /// <summary>
    /// Verifies that <c>Portia.Fitz</c>'s <c>AddPortiaFleetPartitionRunner</c> resolves a typed
    /// workload in its own scope while the partition lease is held.
    /// </summary>
    [Fact]
    public async Task ShouldRunScopedPartitionWorkloadWhenFleetHostedServiceStarts()
    {
        var state = new HostingWorkloadState();
        var services = new ServiceCollection();
        _ = services.AddSingleton<IPartitionLeaseCompetitor>(new InMemoryLeaseClient());
        _ = services.AddSingleton(state);
        _ = services.AddScoped<HostingPartitionWorkload>();
        _ = services.AddSingleton<IFleetMembership, SingleWorkerMembership>();
        _ = services.AddPortiaFleetPartitionRunner<HostingPartitionWorkload>(
            ["lease://portia/fleet/partition-a"],
            options: SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var hostedService = Assert.Single(provider.GetServices<IHostedService>());
        await hostedService.StartAsync(default);
        await WaitUntilAsync(() => state.StartedPartitions.Count == 1);
        await hostedService.StopAsync(default);

        Assert.Equal(["lease://portia/fleet/partition-a"], state.StartedPartitions);
        Assert.Equal(["lease://portia/fleet/partition-a"], state.StoppedPartitions);
        _ = Assert.Single(state.WorkloadInstances);
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
        ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), aggregateId, aggregateVersion, DateTimeOffset.UtcNow));
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
        public RequestMetadata Metadata { get; } = RequestMetadata.Create();
        public RequestInvocation Invocation => new QueueInvocation("queue://test/work/item", Attempt);
        public IRequest Request { get; } = request;

        public string? ActorToken => "valid-token";

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

sealed class HostingWorkloadState
{
    public ConcurrentQueue<TenantId> StartedTenants { get; } = [];

    public ConcurrentQueue<TenantId> StoppedTenants { get; } = [];

    public ConcurrentQueue<string> StartedPartitions { get; } = [];

    public ConcurrentQueue<string> StoppedPartitions { get; } = [];

    public ConcurrentQueue<Guid> WorkloadInstances { get; } = [];
}

sealed class HostingTenantWorkload(HostingWorkloadState state) : ITenantWorkload, IAsyncDisposable
{
    readonly Guid _instanceId = Guid.NewGuid();
    TenantId? _tenantId;

    public async Task RunAsync(TenantId tenantId, CancellationToken ct)
    {
        _tenantId = tenantId;
        state.StartedTenants.Enqueue(tenantId);
        state.WorkloadInstances.Enqueue(_instanceId);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when the tenant stops or the host shuts down.
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_tenantId is { } tenantId)
            state.StoppedTenants.Enqueue(tenantId);

        return ValueTask.CompletedTask;
    }
}

sealed class HostingPartitionWorkload(HostingWorkloadState state) : IPartitionWorkload, IAsyncDisposable
{
    readonly Guid _instanceId = Guid.NewGuid();
    string? _partition;

    public async Task RunAsync(string partition, LeaseAuthority authority, CancellationToken ct)
    {
        _partition = partition;
        state.StartedPartitions.Enqueue(partition);
        state.WorkloadInstances.Enqueue(_instanceId);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when the lease is lost or the host shuts down.
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_partition is { } partition)
            state.StoppedPartitions.Enqueue(partition);

        return ValueTask.CompletedTask;
    }
}

sealed class LateFaultProjectionTarget : ITestProjectionRepository
{
    ProjectionCheckpoint _checkpoint;
    public TestProjection Projection { get; } = new();

    public int CommitAttempts { get; private set; }

    public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(
        CheckpointIdentity identity,
        CancellationToken ct = default) => ValueTask.FromResult(_checkpoint);

    public ValueTask<IProjectionBatch> BeginAsync(
        ProjectionBatchContext context,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IProjectionBatch>(new LateFaultProjectionBatch(this));

    sealed class LateFaultProjectionBatch(LateFaultProjectionTarget target) : IProjectionBatch
    {
        public TestProjection Projection => target.Projection;

        public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
        {
            target._checkpoint = checkpoint;
            var commitAttempt = ++target.CommitAttempts;

            return commitAttempt == 1
                ? ValueTask.FromException(new InvalidOperationException("Simulated late commit acknowledgement failure."))
                : ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

sealed class TransientReloadFailureProjectionTarget : ITestProjectionRepository
{
    readonly TaskCompletionSource _checkpointReloaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    ProjectionCheckpoint _checkpoint;

    public TestProjection Projection { get; } = new();

    public int LoadAttempts { get; private set; }

    public int CommitAttempts { get; private set; }

    public Task CheckpointReloaded => _checkpointReloaded.Task;

    public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(
        CheckpointIdentity identity,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        LoadAttempts++;

        if (LoadAttempts == 2)
        {
            return ValueTask.FromException<ProjectionCheckpoint>(
                new InvalidOperationException("Simulated transient checkpoint reload failure."));
        }

        if (LoadAttempts == 3)
            _ = _checkpointReloaded.TrySetResult();

        return ValueTask.FromResult(_checkpoint);
    }

    public ValueTask<IProjectionBatch> BeginAsync(
        ProjectionBatchContext context,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IProjectionBatch>(new TransientReloadFailureProjectionBatch(this));

    sealed class TransientReloadFailureProjectionBatch(TransientReloadFailureProjectionTarget target)
        : IProjectionBatch
    {
        public TestProjection Projection => target.Projection;

        public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
        {
            target._checkpoint = checkpoint;
            target.CommitAttempts++;

            return target.CommitAttempts == 1
                ? ValueTask.FromException(new InvalidOperationException("Simulated late commit acknowledgement failure."))
                : ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
