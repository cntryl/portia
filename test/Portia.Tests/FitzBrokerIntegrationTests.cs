using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using Cntryl.Fitz;
using Cntryl.Fitz.Abstractions.Domains.Kv;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Proves Portia's Fitz adapters against the real broker and real .NET Fitz client rather than
///     the focused in-memory fakes used by the unit tests.
/// </summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
public sealed class FitzBrokerIntegrationTests(FitzBrokerFixture broker)
{
    readonly FitzBrokerFixture _broker = broker;

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
