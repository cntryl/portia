using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using Cntryl.Fitz;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Proves Portia's Fitz adapters against the real broker and real .NET Fitz client rather than
///     the focused in-memory fakes used by the unit tests.
/// </summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
[Trait("Category", "BrokerIntegration")]
public sealed class FitzBrokerIntegrationTests(FitzBrokerFixture broker)
{
    readonly FitzBrokerFixture _broker = broker;

    /// <summary>Two replicas upsert one route, changed desired state replaces it, and cancellation removes it.</summary>
    [Fact]
    public async Task ShouldUpsertAndCancelDeclarativeScheduleAgainstRealFitzBroker()
    {
        await using var firstClient = await _broker.CreateClientAsync();
        await using var secondClient = await _broker.CreateClientAsync();
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var first = new FitzRequestScheduler(firstClient.Schedule, serializer);
        var second = new FitzRequestScheduler(secondClient.Schedule, serializer);
        const string route = "schedule://test/shared/action/run";

        try
        {
            Assert.Equal(route, await first.EnsureAsync(new UniversalAction(1),
                new RequestScheduleSpec("0 0 * * *"), RequestRouteValues.None,
                RequestActor.CreateSystem("replica")));
            Assert.Equal(route, await second.EnsureAsync(new UniversalAction(1),
                new RequestScheduleSpec("0 0 * * *"), RequestRouteValues.None,
                RequestActor.CreateSystem("replica")));
            Assert.Single(await firstClient.Schedule.ListBySelectorAsync(route));

            _ = await second.EnsureAsync(new UniversalAction(2), new RequestScheduleSpec("0 1 * * *"),
                RequestRouteValues.None, RequestActor.CreateSystem("replica"));
            var updated = Assert.Single(await firstClient.Schedule.ListBySelectorAsync(route));
            var outer = System.Text.Json.JsonSerializer.Deserialize(updated.Payload.Span,
                FitzJsonContext.Default.FitzScheduledRequestEnvelope)!;
            var envelope = serializer.DeserializeEnvelope(outer.RequestEnvelope);
            Assert.Equal(2, Assert.IsType<UniversalAction>(envelope.Request).Value);
        }
        finally
        {
            await firstClient.Schedule.CancelAsync(route);
        }

        Assert.Empty(await firstClient.Schedule.ListBySelectorAsync(route));
    }

    /// <summary>A real persisted schedule carries the completed producer span as its future link.</summary>
    [Fact]
    public async Task ShouldPersistScheduleProducerContextAgainstRealFitzBroker()
    {
        using var listener = ListenToRequests(out var activities);
        await using var client = await _broker.CreateClientAsync();
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var scheduler = new FitzRequestScheduler(client.Schedule, serializer);
        const string route = "schedule://test/shared/action/run";
        string? scheduleId = null;
        try
        {
            scheduleId = await scheduler.ScheduleAsync(new UniversalAction(73),
                new RequestScheduleSpec("0 0 * * *"), RequestRouteValues.None,
                RequestActor.CreateSystem("telemetry-test"));
            var stored = Assert.Single(await client.Schedule.ListBySelectorAsync(route));
            var scheduled = System.Text.Json.JsonSerializer.Deserialize(stored.Payload.Span,
                FitzJsonContext.Default.FitzScheduledRequestEnvelope)!;
            var envelope = serializer.DeserializeEnvelope(scheduled.RequestEnvelope);
            var producer = Assert.Single(activities,
                activity => activity.OperationName == PortiaTelemetry.SendActivityName &&
                            Equals(activity.GetTagItem("portia.transport.name"), "schedule"));

            Assert.Equal(producer.Id, envelope.TraceContext?.TraceParent);
            Assert.Equal(producer.TraceStateString, envelope.TraceContext?.TraceState);
        }
        finally
        {
            if (scheduleId is not null)
            {
                await scheduler.CancelAsync(scheduleId);
            }
        }
    }

    /// <summary>
    ///     Runs the public event-store conformance suite against the real broker, so FitzEventStore
    ///     is held to the same contract a third-party adapter would be — in particular that a stale
    ///     append surfaces as EventStreamConcurrencyException and writes nothing.
    /// </summary>
    [Fact]
    public async Task ShouldSatisfyEventStoreConformanceAgainstRealFitzBroker()
    {
        await using var client = await _broker.CreateClientAsync();
        await EventStoreConformance.VerifyAsync(new FitzEventStoreProbe(client));
    }

    /// <summary>Runs the distributed ownership contract against two real Fitz client sessions.</summary>
    [Fact]
    public async Task ShouldSatisfyWorkloadCoordinatorConformanceAgainstRealFitzBroker() =>
        await WorkloadCoordinatorConformance.VerifyAsync(new FitzWorkloadCoordinatorProbe(_broker));

    /// <summary>
    ///     Runs the public projection-store conformance suite against <see cref="FitzKvProjectionStore" />
    ///     and a real broker, so the adapter consumers actually deploy is held to the same atomicity,
    ///     stale-checkpoint, and generation-isolation contract as the in-memory fake — in particular that
    ///     two concurrent writers of one checkpoint really do conflict in Fitz KV rather than silently
    ///     letting the loser's stale commit through and replaying the projection.
    /// </summary>
    [Fact]
    public async Task ShouldSatisfyProjectionStoreConformanceAgainstRealFitzKv() =>
        await ProjectionStoreConformance.VerifyAsync(new FitzKvProjectionProbe(_broker));

    /// <summary>
    ///     Verifies durable reactor progress against the real broker. A reactor's checkpoint is the only
    ///     thing standing between a restart and reissuing every external effect it has already caused,
    ///     and unlike the projection store it has no conformance suite behind it — so the round trip, the
    ///     unwritten-identity default, and separation by identity are asserted directly here.
    /// </summary>
    [Fact]
    public async Task ShouldPersistReactorCheckpointsThroughRealFitzKv()
    {
        await using var client = await _broker.CreateClientAsync();
        var route = "kv://portia-integration/conformance/" + Uuid.CreateVersion4();
        var store = new FitzKvCheckpointStore(client.Kv, route);
        var pattern = EventStreamPattern.ForPattern("portia-integration", "reactions");
        var live = new CheckpointIdentity("reactor", pattern);
        var rebuild = new CheckpointIdentity("reactor", pattern, "rebuild-1");

        Assert.Equal(ProjectionCheckpoint.Start, await store.LoadAsync(live));

        await store.SaveAsync(live, new ProjectionCheckpoint(42));

        Assert.Equal(42ul, (await store.LoadAsync(live)).NextOffset);
        // A rebuild generation shares the route and must not inherit the live generation's progress.
        Assert.Equal(ProjectionCheckpoint.Start, await store.LoadAsync(rebuild));

        await store.SaveAsync(live, new ProjectionCheckpoint(43));

        Assert.Equal(43ul, (await store.LoadAsync(live)).NextOffset);
    }

    /// <summary>
    ///     Verifies that a Portia domain event survives an append/read round trip through Fitz.
    /// </summary>
    [Fact]
    public async Task ShouldRoundTripDomainEventThroughRealFitzBroker()
    {
        await using var client = await _broker.CreateClientAsync();
        var serializer = TestJson.DomainSerializer(
            new DomainEventTypeCatalog().Register<ValueChanged>(1, "test.value.changed"));
        var store = new FitzEventStore(client.Stream, serializer);
        var aggregateId = Uuid.CreateVersion4();
        var stream = new EventStreamAddress(
            "portia-integration",
            "event-store",
            aggregateId.ToString());
        var original = new ValueChanged(42);
        original.AttachMetadata(new DomainEventMetadata(
            Uuid.CreateVersion4(),
            aggregateId,
            1,
            DateTimeOffset.UtcNow));

        await store.AppendAsync(stream, 0, [original]);
        var events = new List<DomainEvent>();
        await foreach (var ev in store.ReadAsync(stream))
            events.Add(ev.Event);

        var roundTripped = Assert.IsType<ValueChanged>(Assert.Single(events));
        Assert.Equal(original.Value, roundTripped.Value);
        Assert.Equal(original.Metadata, roundTripped.Metadata);
    }

    /// <summary>
    ///     Verifies schema evolution against real, persisted bytes rather than an in-memory JSON
    ///     round trip: an event written by an old serializer configuration (only the old CLR type
    ///     registered) is upcast correctly when a different serializer instance — only the new type
    ///     and an upcaster registered, simulating a later deploy with the old type gone entirely —
    ///     reads that same real Fitz stream back.
    /// </summary>
    [Fact]
    public async Task ShouldUpcastEventReadFromRealFitzStreamWrittenByOlderSchemaVersion()
    {
        await using var client = await _broker.CreateClientAsync();
        var aggregateId = Uuid.CreateVersion4();
        var stream = new EventStreamAddress("portia-integration", "event-store-evolution", aggregateId.ToString());

        var writerStore = new FitzEventStore(client.Stream,
            TestJson.DomainSerializer(new DomainEventTypeCatalog().Register<WidgetNamed>(1, "WidgetNamed")));
        var original = new WidgetNamed("Sprocket");
        original.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), aggregateId, 1, DateTimeOffset.UtcNow));
        await writerStore.AppendAsync(stream, 0, [original]);

        var readerStore = new FitzEventStore(
            client.Stream,
            TestJson.DomainSerializer(
                new DomainEventTypeCatalog().Register<WidgetRenamed>(2, "WidgetNamed"),
                [new WidgetNamedToRenamedUpcaster()]));

        var events = new List<DomainEvent>();
        await foreach (var ev in readerStore.ReadAsync(stream))
            events.Add(ev.Event);

        var widget = Assert.IsType<WidgetRenamed>(Assert.Single(events));
        Assert.Equal("Sprocket", widget.DisplayName);
    }

    /// <summary>
    ///     Verifies that a stale writer against a real Fitz stream is rejected as
    ///     <see cref="EventStreamConcurrencyException" /> — the same type
    ///     <see cref="InMemoryEventStore" /> throws for the identical situation — rather than an
    ///     unnormalized, Fitz-specific exception a caller has no stable way to catch and retry on.
    ///     Retained as an acceptance gate: the pinned Fitz 0.1.1 client currently supplies no
    ///     DomainCode for APPEND, so strict structured classification leaves this assertion failing
    ///     until the upstream protocol/client carries code 2001. Do not reintroduce wording matching.
    /// </summary>
    [Fact]
    public async Task ShouldThrowConcurrencyExceptionWhenAppendingWithStaleExpectedVersion()
    {
        await using var client = await _broker.CreateClientAsync();
        var serializer =
            TestJson.DomainSerializer(new DomainEventTypeCatalog().Register<ValueChanged>(1, "test.value.changed"));
        var store = new FitzEventStore(client.Stream, serializer);
        var aggregateId = Uuid.CreateVersion4();
        var stream = new EventStreamAddress("portia-integration", "event-store-conflict", aggregateId.ToString());
        var first = new ValueChanged(1);
        first.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), aggregateId, 1, DateTimeOffset.UtcNow));
        await store.AppendAsync(stream, 0, [first]);

        var stale = new ValueChanged(2);
        stale.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), aggregateId, 1, DateTimeOffset.UtcNow));

        _ = await Assert.ThrowsAsync<EventStreamConcurrencyException>(() =>
            store.AppendAsync(stream, 0, [stale]).AsTask());
    }

    /// <summary>
    ///     Verifies that Portia's RPC sender and server exchange a typed request and result through
    ///     two real Fitz client sessions.
    /// </summary>
    [Fact]
    public async Task ShouldRoundTripRequestThroughRealFitzBroker()
    {
        using var activityListener = ListenToRequests(out var activities);
        using var meterListener = ListenToDeliveries(out var deliveries);
        await using var workerClient = await _broker.CreateClientAsync();
        await using var callerClient = await _broker.CreateClientAsync();
        var serializer = TestJson.Serializer(typeof(RpcGetValue));
        using var busHost = TestRequestBus.Create();
        var server = new FitzRpcRequestServer(
            workerClient.Rpc,
            busHost.ScopeFactory);
        await using var registration = await server.RegisterAsync<RpcGetValue, int>();
        var sender = new FitzRemoteRequestSender(callerClient.Rpc, serializer, serializer);

        var result = await sender.SendAsync<RpcGetValue, int>(
            new RpcGetValue(),
            new RequestRouteValues(),
            null);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
        AssertParentedFitzTopology(activities, "test.rpc.get-value", "rpc");
        AssertCompletedDelivery(deliveries, "test.rpc.get-value", "rpc");
    }

    /// <summary>A real queue delivery starts a linked root and records one completed reservation.</summary>
    [Fact]
    public async Task ShouldEmitLinkedQueueTopologyAgainstRealFitzBroker()
    {
        await using var workerClient = await _broker.CreateClientAsync();
        await using var callerClient = await _broker.CreateClientAsync();
        const string route = "queue://test/shared/action";
        await DrainQueueAsync(workerClient.Queue, route);
        using var activityListener = ListenToRequests(out var activities);
        using var meterListener = ListenToDeliveries(out var deliveries);
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var handler = new UniversalActionHandler();
        using var busHost = TestRequestBus.Create(universalActionHandler: handler);
        var consumer = new FitzRequestQueueConsumer(workerClient.Queue, serializer, route, 5,
            waitDuration: TimeSpan.FromMilliseconds(100));
        var runner = new QueueRunner(new OneQueueConsumer(consumer),
            RequestDeliveryScopes.FixedQueue(busHost.Bus, new TestRequestActorValidator()));
        var publisher = new FitzRequestQueuePublisher(callerClient.Queue, serializer);

        await publisher.EnqueueAsync(new UniversalAction(74), RequestRouteValues.None, "valid-token");
        await runner.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(74, handler.HandledValues);
        AssertLinkedFitzTopology(activities, "test.shared.universal-action", "queue");
        AssertCompletedDelivery(deliveries, "test.shared.universal-action", "queue");
    }

    /// <summary>A real notice delivery starts a linked root and records one completed delivery.</summary>
    [Fact]
    public async Task ShouldEmitLinkedNoticeTopologyAgainstRealFitzBroker()
    {
        await using var workerClient = await _broker.CreateClientAsync();
        await using var callerClient = await _broker.CreateClientAsync();
        using var activityListener = ListenToRequests(out var activities);
        using var meterListener = ListenToDeliveries(out var deliveries);
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var handler = new UniversalActionHandler();
        using var busHost = TestRequestBus.Create(universalActionHandler: handler);
        var signalingNotice = new SignalingNoticeClient(workerClient.Notice);
        var consumer = new FitzNoticeRequestConsumer(signalingNotice, serializer, "notice://test/shared/action");
        var runner = new RequestNotificationRunner(new OneNotificationConsumer(consumer),
            RequestDeliveryScopes.Fixed(busHost.Bus, new TestRequestActorValidator()));
        var run = runner.RunAsync();
        await signalingNotice.Ready.WaitAsync(TimeSpan.FromSeconds(10));

        await new FitzNoticeRequestSender(callerClient.Notice, serializer)
            .PublishAsync(new UniversalAction(75), RequestRouteValues.None, "valid-token");
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(75, handler.HandledValues);
        AssertLinkedFitzTopology(activities, "test.shared.universal-action", "notice");
        AssertCompletedDelivery(deliveries, "test.shared.universal-action", "notice");
    }

    /// <summary>
    ///     Verifies that calling a route with no registered worker fails fast and clearly, rather
    ///     than hanging — confirming, against a real broker, that <see cref="FitzRemoteRequestSender" />
    ///     deliberately imposing no timeout of its own (see its own remarks) is a safe choice: Fitz
    ///     itself already fails a call to an unregistered route in milliseconds, not by hanging until
    ///     some caller-supplied deadline.
    /// </summary>
    [Fact]
    public async Task ShouldFailFastWhenNoWorkerIsRegisteredForRoute()
    {
        await using var client = await _broker.CreateClientAsync();
        var serializer = TestJson.Serializer(typeof(NoWorkerRegisteredPing));
        var sender = new FitzRemoteRequestSender(client.Rpc, serializer, serializer);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var elapsed = Stopwatch.StartNew();
        _ = await Assert.ThrowsAnyAsync<Exception>(() =>
            sender.SendAsync(new NoWorkerRegisteredPing(), new RequestRouteValues(), null, cts.Token).AsTask());

        // Not just "it eventually throws before the 10s cap" — genuinely fast, proving this
        // isn't relying on the test's own cancellation to end the call.
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2),
            $"Took {elapsed.Elapsed} — too close to looking like a hang.");
    }

    static ActivityListener ListenToRequests(out ConcurrentBag<Activity> activities)
    {
        var captured = new ConcurrentBag<Activity>();
        activities = captured;
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PortiaTelemetry.SourceName,
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Add
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    static MeterListener ListenToDeliveries(out ConcurrentBag<DeliveryMeasurement> deliveries)
    {
        var captured = new ConcurrentBag<DeliveryMeasurement>();
        deliveries = captured;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == PortiaTelemetry.SourceName &&
                    instrument.Name == "portia.request.delivery.count")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            captured.Add(new DeliveryMeasurement(value, tags.ToArray())));
        listener.Start();
        return listener;
    }

    static void AssertParentedFitzTopology(IEnumerable<Activity> captured, string requestName, string transport)
    {
        var activities = captured.Where(activity =>
            Equals(activity.GetTagItem("portia.request.name"), requestName)).ToArray();
        var send = Assert.Single(activities, activity => activity.OperationName == PortiaTelemetry.SendActivityName);
        var process = Assert.Single(activities,
            activity => activity.OperationName == PortiaTelemetry.ProcessActivityName);
        var execute = Assert.Single(activities,
            activity => activity.OperationName == PortiaTelemetry.ExecuteActivityName);
        Assert.Equal(send.TraceId, process.TraceId);
        Assert.Equal(send.SpanId, process.ParentSpanId);
        Assert.Equal(process.TraceId, execute.TraceId);
        Assert.Equal(process.SpanId, execute.ParentSpanId);
        AssertBoundedFitzTags(send, process, execute, transport);
    }

    static void AssertLinkedFitzTopology(IEnumerable<Activity> captured, string requestName, string transport)
    {
        var activities = captured.Where(activity =>
            Equals(activity.GetTagItem("portia.request.name"), requestName)).ToArray();
        var send = Assert.Single(activities, activity => activity.OperationName == PortiaTelemetry.SendActivityName);
        var process = Assert.Single(activities,
            activity => activity.OperationName == PortiaTelemetry.ProcessActivityName);
        var execute = Assert.Single(activities,
            activity => activity.OperationName == PortiaTelemetry.ExecuteActivityName);
        Assert.NotEqual(send.TraceId, process.TraceId);
        var link = Assert.Single(process.Links);
        Assert.Equal(send.TraceId, link.Context.TraceId);
        Assert.Equal(send.SpanId, link.Context.SpanId);
        Assert.Equal(process.TraceId, execute.TraceId);
        Assert.Equal(process.SpanId, execute.ParentSpanId);
        AssertBoundedFitzTags(send, process, execute, transport);
    }

    static void AssertBoundedFitzTags(Activity send, Activity process, Activity execute, string transport)
    {
        Assert.Equal(ActivityKind.Producer, send.Kind);
        Assert.Equal(ActivityKind.Consumer, process.Kind);
        Assert.Equal(ActivityKind.Internal, execute.Kind);
        Assert.Equal(
            ["portia.request.name", "portia.transport.name", "messaging.system", "messaging.operation.type", "portia.outcome"],
            send.TagObjects.Select(tag => tag.Key));
        Assert.Equal(
            ["portia.request.name", "portia.transport.name", "messaging.system", "messaging.operation.type", "portia.outcome"],
            process.TagObjects.Select(tag => tag.Key));
        Assert.Equal(["portia.request.name", "portia.transport.name", "portia.outcome"],
            execute.TagObjects.Select(tag => tag.Key));
        Assert.All(new[] { send, process, execute }, activity =>
        {
            Assert.Equal(transport, activity.GetTagItem("portia.transport.name"));
            Assert.Equal("success", activity.GetTagItem("portia.outcome"));
            Assert.DoesNotContain(activity.TagObjects, tag =>
                tag.Value is string value && value.Contains("://", StringComparison.Ordinal));
        });
    }

    static void AssertCompletedDelivery(IEnumerable<DeliveryMeasurement> captured, string requestName,
        string transport)
    {
        var delivery = Assert.Single(captured);
        Assert.Equal(1, delivery.Value);
        Assert.Equal(["portia.request.name", "portia.transport.name", "portia.outcome"],
            delivery.Tags.Select(tag => tag.Key));
        Assert.Equal([requestName, transport, "completed"], delivery.Tags.Select(tag => tag.Value));
    }

    static async Task DrainQueueAsync(IQueueClient queue, string route)
    {
        while (true)
        {
            var items = await queue.ReserveAsync(route, TimeSpan.FromSeconds(1), wait: TimeSpan.Zero);
            if (items.Length == 0)
            {
                return;
            }

            foreach (var item in items)
            {
                await item.CompleteAsync();
            }
        }
    }

    readonly record struct DeliveryMeasurement(long Value, KeyValuePair<string, object?>[] Tags);

    sealed class OneQueueConsumer(IRequestQueueConsumer inner) : IRequestQueueConsumer
    {
        public async IAsyncEnumerable<IQueuedRequest> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var item in inner.ReadAsync(ct).WithCancellation(ct))
            {
                yield return item;
                yield break;
            }
        }
    }

    sealed class OneNotificationConsumer(IRequestNotificationConsumer inner) : IRequestNotificationConsumer
    {
        public async IAsyncEnumerable<RequestNotification> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var item in inner.ReadAsync(ct).WithCancellation(ct))
            {
                yield return item;
                yield break;
            }
        }
    }

    sealed class SignalingNoticeClient(INoticeClient inner) : INoticeClient
    {
        readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Ready => _ready.Task;

        public Task PublishAsync(string route, ReadOnlyMemory<byte> body, CancellationToken ct = default) =>
            inner.PublishAsync(route, body, ct);

        public async Task<NoticeSubscription> SubscribeAsync(string selector, CancellationToken ct = default)
        {
            var subscription = await inner.SubscribeAsync(selector, ct).ConfigureAwait(false);
            _ready.TrySetResult();
            return subscription;
        }
    }

    sealed class FitzEventStoreProbe(Client client) : IEventStoreConformanceProbe
    {
        public string Realm => "portia-integration";

        /// <summary>A fresh area per run, so isolation needs no destructive broker operation.</summary>
        public string Area { get; } = "conformance-" + Uuid.CreateVersion4();

        public ValueTask ResetAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<IEventStore> OpenAsync(CancellationToken ct = default) => ValueTask.FromResult<IEventStore>(
            new FitzEventStore(client.Stream, TestJson.DomainSerializer(
                new DomainEventTypeCatalog().Register<ConformanceEvent>(1, "portia.conformance.event"))));
    }

    sealed class FitzWorkloadCoordinatorProbe(FitzBrokerFixture broker)
        : IWorkloadCoordinatorConformanceProbe
    {
        string _membershipSelector = string.Empty;

        public TimeSpan ConvergenceTimeout => TimeSpan.FromSeconds(10);

        public TimeSpan StabilityWindow => TimeSpan.FromMilliseconds(500);

        public ValueTask ResetAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _membershipSelector = $"lease://portia-integration/conformance-{Uuid.CreateVersion4()}/*";
            return ValueTask.CompletedTask;
        }

        public async ValueTask<IWorkloadCoordinatorConformanceWorker> OpenWorkerAsync(
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var client = await broker.CreateClientAsync();
            try
            {
                var connection = new FitzApplicationConnection(client, false, TimeSpan.Zero);
                var configuration = new PortiaFitzBuilder(new ServiceCollection().AddPortia());
                _ = configuration.UseFleet(new FleetRunOptions
                {
                    MembershipSelector = _membershipSelector,
                    LeaseTtl = TimeSpan.FromSeconds(3),
                    ReconciliationInterval = TimeSpan.FromMilliseconds(100),
                    PartitionStopTimeout = TimeSpan.FromSeconds(2)
                });
                return new FitzWorkloadCoordinatorWorker(
                    client,
                    connection,
                    new FitzWorkloadCoordinator(connection, configuration, null, null));
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }
    }

    sealed class FitzWorkloadCoordinatorWorker(
        Client client,
        FitzApplicationConnection connection,
        IWorkloadCoordinator coordinator) : IWorkloadCoordinatorConformanceWorker
    {
        public IWorkloadCoordinator Coordinator { get; } = coordinator;

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            await client.DisposeAsync();
        }
    }

    sealed class FitzKvProjectionProbe(FitzBrokerFixture broker) : IProjectionStoreConformanceProbe
    {
        readonly string _route = "kv://portia-integration/conformance/" + Uuid.CreateVersion4();

        public CheckpointIdentity LiveIdentity { get; } = new(
            "conformance", EventStreamPattern.ForPattern("portia-integration", "projection"));

        public CheckpointIdentity RebuildIdentity { get; } = new(
            "conformance", EventStreamPattern.ForPattern("portia-integration", "projection"), "rebuild-1");

        // Every run gets its own route, so the suite starts empty without deleting a shared one.
        public ValueTask ResetAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        // Each session gets its own Fitz client: the suite's stale-checkpoint check needs two
        // genuinely independent writers, and Fitz KV allows one read-write transaction per resource
        // per session — two stores sharing one connection fail at BEGIN instead of racing, which is
        // the deployment shape a workload coordinator already prevents.
        public async ValueTask<IProjectionStoreConformanceSession> OpenSessionAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var client = await broker.CreateClientAsync();
            try
            {
                return new FitzKvProjectionSession(client, new FitzKvValueRepository(client.Kv, _route));
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }
    }

    // One independent repository session: its own FitzKvProjectionStore instance, so each session owns
    // one Fitz KV transaction exactly as a scoped application repository would. Begin, commit, and
    // rollback all run through the real store; this only arms the suite's single injected failure.
    sealed class FitzKvProjectionSession(Client client, FitzKvValueRepository repository)
        : IProjectionStoreConformanceSession, IProjectionStore
    {
        CheckpointIdentity? _identity;
        bool _failNextCommit;

        public IProjectionStore Store => this;

        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity,
            CancellationToken ct = default) => repository.LoadCheckpointAsync(identity, ct);

        public async ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            _identity = context.Identity;
            return new FailableBatch(this, await repository.BeginAsync(context, ct));
        }

        public ValueTask StageValueAsync(string value, CancellationToken ct = default) =>
            new(repository.StageAsync(_identity!, value, ct));

        public ValueTask<string?> ReadValueAsync(CheckpointIdentity identity, CancellationToken ct = default) =>
            repository.ReadAsync(identity, ct);

        public ValueTask FailNextCommitAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _failNextCommit = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => client.DisposeAsync();

        // Injects the suite's one commit failure at the Portia boundary; the real store's rollback
        // still runs when the suite disposes the batch, which is the behavior under test.
        sealed class FailableBatch(FitzKvProjectionSession session, IProjectionBatch inner) : IProjectionBatch
        {
            public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
            {
                if (session._failNextCommit)
                {
                    session._failNextCommit = false;
                    throw new IOException("Injected projection commit failure.");
                }

                return inner.CommitAsync(checkpoint, ct);
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    // A real FitzKvProjectionStore subclass that writes one application value through the shared
    // transaction, so the suite's atomicity checks cover domain data and checkpoint together.
    sealed class FitzKvValueRepository(IKvClient kv, string route) : FitzKvProjectionStore(kv, route)
    {
        readonly IKvClient _kv = kv;
        readonly string _route = route;

        public Task StageAsync(CheckpointIdentity identity, string value, CancellationToken ct) =>
            Transaction.PutAsync(ValueKey(identity), Encoding.UTF8.GetBytes(value), ct);

        public async ValueTask<string?> ReadAsync(CheckpointIdentity identity, CancellationToken ct)
        {
            await using var tx = await _kv.BeginAsync(_route, KvDurability.Sync, KvMode.ReadOnly, ct);
            var result = await tx.GetAsync(ValueKey(identity), ct);
            return result.Found ? Encoding.UTF8.GetString(result.Value!.Value.Span) : null;
        }

        static ReadOnlyMemory<byte> ValueKey(CheckpointIdentity identity) =>
            Encoding.UTF8.GetBytes(string.Join('\0', "value", identity.ComponentName, identity.Pattern,
                identity.RebuildId ?? string.Empty));
    }

    sealed class AlwaysValidActorValidator : IRequestActorValidator
    {
        public ValueTask<Result<ClaimsPrincipal>> ValidateAsync(
            string? token,
            CancellationToken ct = default) =>
            ValueTask.FromResult(Result<ClaimsPrincipal>.Success(RequestActor.System));
    }
}

// Deliberately never registered by any test — the whole point is a route no worker answers.
