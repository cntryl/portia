using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
///     Verifies that <c>Portia.DependencyInjection</c>'s (and, for
///     <see cref="FleetPartitionRunner" />, <c>Portia.Fitz</c>'s) hosting extensions actually start
///     and stop the runner they wire up — not just that DI resolves it, but that a real
///     <see cref="IHostedService" /> calling <c>StartAsync</c>/<c>StopAsync</c> makes the runner do
///     its job and shut down cleanly. Before this, nothing in Portia called any runner's
///     <c>RunAsync</c> automatically at all; an app had to write this glue itself.
/// </summary>
public sealed class PortiaHostingServiceCollectionExtensionsTests
{
    /// <summary>Schedule declarations do not add startup work to API-only hosts.</summary>
    [Fact]
    public void ShouldLeaveStartupSchedulesDormantWithoutWorkers()
    {
        var scheduler = new RecordingStartupScheduler();
        var services = new ServiceCollection();
        _ = services.AddSingleton<IRequestScheduler>(scheduler);
        _ = services.AddPortia().AddRequestSchedule(new FitzHostedRequest(),
            new RequestScheduleSpec("0 0 * * *"), new RequestRouteValues("tenant-123"), RequestActor.System);
        using var provider = services.BuildServiceProvider();

        Assert.DoesNotContain(provider.GetServices<IHostedService>(), service => service is RequestScheduleStartupService);
        Assert.Empty(scheduler.Requests);
    }

    /// <summary>Worker startup ensures declarations once and in registration order.</summary>
    [Fact]
    public async Task ShouldEnsureStartupSchedulesSequentiallyWhenWorkersStart()
    {
        var scheduler = new RecordingStartupScheduler();
        var services = new ServiceCollection();
        _ = services.AddSingleton<IRequestScheduler>(scheduler);
        _ = services.AddPortia()
            .AddRequestSchedule(new FitzHostedRequest(), new RequestScheduleSpec("0 0 * * *"),
                new RequestRouteValues("first"), RequestActor.System)
            .AddRequestSchedule(new FitzHostedRequest(), new RequestScheduleSpec("0 1 * * *"),
                new RequestRouteValues("second"), RequestActor.System)
            .AddWorkers();
        using var provider = services.BuildServiceProvider();
        var hosted = Assert.Single(provider.GetServices<IHostedService>(),
            service => service is RequestScheduleStartupService);

        await hosted.StartAsync(default);

        Assert.Equal(["first", "second"], scheduler.Requests);
    }

    /// <summary>A declared schedule fails startup clearly when no provider was composed.</summary>
    [Fact]
    public async Task ShouldFailWorkerStartupWhenScheduleProviderIsMissing()
    {
        var services = new ServiceCollection();
        _ = services.AddPortia().AddRequestSchedule(new FitzHostedRequest(),
            new RequestScheduleSpec("0 0 * * *"), new RequestRouteValues("tenant-123"), RequestActor.System)
            .AddWorkers();
        using var provider = services.BuildServiceProvider();
        var hosted = Assert.Single(provider.GetServices<IHostedService>(),
            service => service is RequestScheduleStartupService);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => hosted.StartAsync(default));

        Assert.Contains(nameof(IRequestScheduler), error.Message, StringComparison.Ordinal);
    }

    /// <summary>Invalid declarative identities fail startup before reaching the provider.</summary>
    [Fact]
    public async Task ShouldFailWorkerStartupGivenNonSystemScheduleActor()
    {
        var scheduler = new RecordingStartupScheduler();
        var services = new ServiceCollection();
        _ = services.AddSingleton<IRequestScheduler>(scheduler);
        _ = services.AddPortia().AddRequestSchedule(new FitzHostedRequest(),
            new RequestScheduleSpec("0 0 * * *"), new RequestRouteValues("tenant-123"), new())
            .AddWorkers();
        using var provider = services.BuildServiceProvider();
        var hosted = Assert.Single(provider.GetServices<IHostedService>(),
            service => service is RequestScheduleStartupService);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => hosted.StartAsync(default));

        Assert.Contains("system actor", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(scheduler.Requests);
    }

    /// <summary>Provider persistence failures propagate and stop worker startup.</summary>
    [Fact]
    public async Task ShouldFailWorkerStartupWhenSchedulePersistenceFails()
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton<IRequestScheduler>(new RecordingStartupScheduler { Failure = new IOException("broker unavailable") });
        _ = services.AddPortia().AddRequestSchedule(new FitzHostedRequest(),
            new RequestScheduleSpec("0 0 * * *"), new RequestRouteValues("tenant-123"), RequestActor.System)
            .AddWorkers();
        using var provider = services.BuildServiceProvider();
        var hosted = Assert.Single(provider.GetServices<IHostedService>(),
            service => service is RequestScheduleStartupService);

        var error = await Assert.ThrowsAsync<IOException>(() => hosted.StartAsync(default));

        Assert.Equal("broker unavailable", error.Message);
    }

    /// <summary>Hosted composition fails before serving when guarded requests have no evaluator.</summary>
    [Fact]
    public async Task ShouldRequirePermissionEvaluatorForGuardedRequestsAtStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Services.AddFrameworkTests();
        using var host = builder.Build();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains(nameof(IPermissionEvaluator), failure.Message, StringComparison.Ordinal);
        var guarded = new[]
        {
            typeof(GetOrder), typeof(GuardedAction), typeof(GuardedAndAuthorizedAction), typeof(GuardedQuery),
            typeof(GuardedSequence), typeof(HttpGuardedAction), typeof(HttpGuardedQueueAction),
            typeof(TelemetryGuardedAction), typeof(TelemetryGuardedSequence)
        }.Select(type => type.FullName!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(guarded, failure.Message[(failure.Message.IndexOf(':') + 1)..].TrimEnd('.').Trim()
            .Split(", ", StringSplitOptions.None));
    }

    /// <summary>A registered evaluator satisfies hosted startup without constructing it eagerly.</summary>
    [Fact]
    public async Task ShouldAcceptGuardedRequestsGivenPermissionEvaluatorAtStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Services.AddFrameworkTests();
        _ = builder.Services.AddScoped<IPermissionEvaluator>(_ =>
            throw new InvalidOperationException("Startup must not resolve a scoped evaluator."));
        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    /// <summary>An unguarded composition has no permission-evaluator requirement.</summary>
    [Fact]
    public async Task ShouldNotRequirePermissionEvaluatorWithoutGuardedRequests()
    {
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Services.AddPortia();
        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    /// <summary>A failing worker contribution leaves the target collection unchanged.</summary>
    [Fact]
    public void ShouldStageAllWorkerRegistrationsBeforeApplyingAny()
    {
        var services = new ServiceCollection();
        var portia = services.AddPortia()
            .ConfigureWorker("first", staged => staged.AddSingleton<StagedWorkerMarker>())
            .ConfigureWorker("broken", Throw);
        var before = services.Count;

        var error = Assert.Throws<InvalidOperationException>(portia.AddWorkers);

        Assert.Equal("broken worker", error.Message);
        Assert.Equal(before, services.Count);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(StagedWorkerMarker));

        static void Throw(IServiceCollection _)
        {
            throw new InvalidOperationException("broken worker");
        }
    }

    /// <summary>A replacement serializer remains outside Portia's JSON-upcaster policy.</summary>
    [Fact]
    public async Task ShouldSkipJsonUpcasterPolicyForCustomSerializer()
    {
        var services = new ServiceCollection();
        _ = services.AddPortia().AddWorkers();
        _ = services.AddSingleton<IJsonDomainEventUpcaster>(new RecordingUpcaster(string.Empty, 0));
        _ = services.AddSingleton<IDomainEventSerializer>(new PassthroughDomainEventSerializer());
        using var provider = services.BuildServiceProvider();

        var validator = Assert.Single(provider.GetServices<IHostedService>(),
            service => service is not BackgroundService);
        await validator.StartAsync(default);

        _ = Assert.IsType<PassthroughDomainEventSerializer>(provider.GetRequiredService<IDomainEventSerializer>());
    }

    /// <summary>API-only hosts validate the default serializer before serving requests.</summary>
    [Fact]
    public async Task ShouldRejectInvalidUpcastersWithoutWorkers()
    {
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Services.AddPortia();
        _ = builder.Services.AddSingleton<IJsonDomainEventUpcaster>(new RecordingUpcaster(string.Empty, 0));
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("Invalid JSON domain-event upcaster registrations", exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>A terminal partition timeout requests host shutdown even when callbacks stay stuck.</summary>
    [Fact]
    public async Task ShouldStopHostGivenHostedPartitionIgnoresCancellation()
    {
        var workload = new StuckPartitionWorkload();
        var services = new ServiceCollection();
        _ = services.AddSingleton(workload);
        using var provider = services.BuildServiceProvider();
        var lifetime = new RecordingApplicationLifetime();
        var options = SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)) with
        {
            PartitionStopTimeout = TimeSpan.FromMilliseconds(50)
        };
        var hosted = new FleetPartitionRunnerHostedService<StuckPartitionWorkload>(
            new FleetPartitionRunner(new InMemoryLeaseClient(), new SingleWorkerMembership()),
            provider.GetRequiredService<IServiceScopeFactory>(),
            ["lease://portia/fleet/stuck-hosted"],
            options,
            lifetime);
        await hosted.StartAsync(default);
        await workload.Started.WaitAsync(TimeSpan.FromSeconds(2));
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await hosted.StopAsync(stopTimeout.Token);
        await lifetime.StopRequested.WaitAsync(TimeSpan.FromSeconds(1));

        workload.Release();
    }

    /// <summary>
    ///     Rejects an invalid reactor batch size during registration rather than deferring the error
    ///     until the hosted service is resolved and enters its retry loop.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShouldRejectNonPositiveReactorBatchSizeDuringRegistration(int maxBatchSize)
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddPortia().AddReactor<TestReactor>(WorkloadScope.Global,
                o => o.Processing = new ProjectionRunOptions { MaxBatchSize = maxBatchSize }));

        Assert.Equal(nameof(ProjectionRunOptions.MaxBatchSize), exception.ParamName);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(WorkloadRegistration));
    }

    /// <summary>
    ///     Verifies that <c>AddPortiaQueueRunner</c> actually dispatches a queued request once the
    ///     hosted service starts.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchQueuedRequestWhenHostedServiceStarts()
    {
        var handler = new ChangeValueHandler();
        var services = new ServiceCollection();
        using var busHost = TestRequestBus.Create(handler);
        _ = services.AddSingleton(busHost.Bus);
        _ = services.AddSingleton<IRequestQueueConsumer>(new HostingFakeQueueConsumer([new ChangeValue(42)]));
        _ = services.AddSingleton<IRequestActorValidator>(new TestRequestActorValidator());
        _ = services.AddPortiaQueueRunner();
        using var provider = services.BuildServiceProvider();

        var hostedService = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        await hostedService.StartAsync(default);
        await WaitUntilAsync(() => handler.LastValue == 42);
        await hostedService.StopAsync(default);

        Assert.Equal(42, handler.LastValue);
    }

    /// <summary>
    ///     Verifies that <c>AddPortiaMultiTenantRunner</c> resolves a typed workload in its own scope
    ///     and disposes that scope when the tenant stops.
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
        using var provider = services.BuildServiceProvider(true);

        var hostedService = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        await hostedService.StartAsync(default);
        await WaitUntilAsync(() => state.StartedTenants.Count == 1);
        await hostedService.StopAsync(default);

        Assert.Equal([new TenantId("acme")], state.StartedTenants);
        Assert.Equal([new TenantId("acme")], state.StoppedTenants);
        _ = Assert.Single(state.WorkloadInstances);
    }

    /// <summary>
    ///     Verifies that <c>AddPortiaProjectorRunner</c> runs a pass and resumes from the checkpoint
    ///     committed atomically by the projection target.
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
        _ = services.AddPortia()
            .AddProjector<TestProjector>(WorkloadScope.Global, o => o.PollInterval = TimeSpan.FromMilliseconds(20))
            .AddWorkers();
        using var provider = services.BuildServiceProvider();

        var hostedService = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        await hostedService.StartAsync(default);
        await WaitUntilAsync(() => target.Projection.Value == 42);
        await hostedService.StopAsync(default);

        Assert.Equal(42, target.Projection.Value);
        Assert.Equal(1UL,
            (await target.LoadCheckpointAsync(new CheckpointIdentity("test-projector",
                EventStreamPattern.ForPattern("test", "projectors")))).NextOffset);
    }

    /// <summary>
    ///     Verifies a late adapter failure after an atomic projection commit reloads the target's
    ///     authoritative checkpoint before retrying, so the committed event is not applied twice.
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
        _ = services.AddPortia()
            .AddProjector<TestProjector>(WorkloadScope.Global, o => o.PollInterval = TimeSpan.FromMilliseconds(10))
            .AddWorkers();
        using var provider = services.BuildServiceProvider();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());

        await hostedService.StartAsync(default);
        await WaitUntilAsync(() => target.CommitAttempts == 1);
        await Task.Delay(50);
        await hostedService.StopAsync(default);

        Assert.Equal(1, target.Projection.HandlerCount);
        Assert.Equal(1UL,
            (await target.LoadCheckpointAsync(new CheckpointIdentity("test-projector",
                EventStreamPattern.ForPattern("test", "projectors")))).NextOffset);
    }

    /// <summary>
    ///     Verifies a transient failure while reloading the authoritative checkpoint remains inside
    ///     the polling loop. The service must retry the load and must not run another projector pass
    ///     from its stale in-memory checkpoint while the target remains unavailable.
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
        _ = services.AddPortia().AddProjector<TestProjector>(WorkloadScope.Global, o =>
        {
            o.Name = "test-projector";
            o.PollInterval = TimeSpan.FromMilliseconds(10);
        }).AddWorkers();
        using var provider = services.BuildServiceProvider();

        var hostedService = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        await hostedService.StartAsync(default);
        var executeTask = hostedService.ExecuteTask
                          ?? throw new InvalidOperationException("The projector hosted service did not start.");
        var firstCompletion = await Task.WhenAny(target.CheckpointReloaded, executeTask);
        Assert.Same(target.CheckpointReloaded, firstCompletion);
        await target.CheckpointReloaded;
        await hostedService.StopAsync(default);

        Assert.False(executeTask.IsFaulted);
        Assert.True(target.LoadAttempts >= 3);
        Assert.Equal(1, target.Projection.HandlerCount);
        Assert.Equal(1UL,
            (await target.LoadCheckpointAsync(new CheckpointIdentity("test-projector",
                EventStreamPattern.ForPattern("test", "projectors")))).NextOffset);
    }

    /// <summary>
    ///     Verifies that <c>Portia.Fitz</c>'s <c>AddPortiaFleetPartitionRunner</c> resolves a typed
    ///     workload in its own scope while the partition lease is held.
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
            SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)));
        using var provider = services.BuildServiceProvider(true);

        var hostedService = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
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
        ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), aggregateId, aggregateVersion,
            DateTimeOffset.UtcNow));
        return ev;
    }

    sealed class HostingFakeQueueConsumer(IReadOnlyList<IRequest> items) : IRequestQueueConsumer
    {
        public async IAsyncEnumerable<IQueuedRequest> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
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
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var tenantId in initial)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return tenantId;
            }
        }

        public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (ct.Register(() => tcs.TrySetResult()))
                await tcs.Task.ConfigureAwait(false);

            yield break;
        }
    }
}

sealed class RecordingStartupScheduler : IRequestScheduler
{
    public List<string> Requests { get; } = [];
    public Exception? Failure { get; init; }

    public ValueTask<string> EnsureAsync<TRequest>(TRequest request, RequestScheduleSpec spec,
        RequestRouteValues routeValues, System.Security.Claims.ClaimsPrincipal actor,
        CancellationToken ct = default) where TRequest : IRequest, ISchedulable
    {
        if (Failure is not null)
            return ValueTask.FromException<string>(Failure);
        Requests.Add(routeValues.Realm!);
        return ValueTask.FromResult(routeValues.Realm!);
    }

    public ValueTask<string> ScheduleAsync<TRequest>(TRequest request, RequestScheduleSpec spec,
        RequestRouteValues routeValues, System.Security.Claims.ClaimsPrincipal actor, RequestMetadata metadata,
        CancellationToken ct = default) where TRequest : IRequest, ISchedulable =>
        throw new NotSupportedException();

    public ValueTask CancelAsync(string scheduleId, CancellationToken ct = default) => ValueTask.CompletedTask;
}
