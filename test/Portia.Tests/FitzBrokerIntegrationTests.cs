namespace Cntryl.Portia;

/// <summary>
/// Proves Portia's Fitz adapters against the real broker and real .NET Fitz client rather than
/// the focused in-memory fakes used by the unit tests.
/// </summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
public sealed class FitzBrokerIntegrationTests(FitzBrokerFixture broker)
{
    readonly FitzBrokerFixture _broker = broker;

    /// <summary>
    /// Verifies that a Portia domain event survives an append/read round trip through Fitz.
    /// </summary>
    [Fact]
    public async Task ShouldRoundTripDomainEventThroughRealFitzBroker()
    {
        await using var client = await _broker.CreateClientAsync();
        var serializer = new JsonDomainEventSerializer(
            new DomainEventTypeCatalog().Register<ValueChanged>());
        var store = new FitzEventStore(client.Stream, serializer);
        var aggregateId = Uuid.CreateVersion7();
        var stream = new EventStreamAddress(
            "portia-integration",
            "event-store",
            aggregateId.ToString());
        var original = new ValueChanged(42);
        original.AttachMetadata(new DomainEventMetadata(
            Uuid.CreateVersion7(),
            aggregateId,
            1,
            DateTimeOffset.UtcNow));

        await store.AppendAsync(stream, 0, [original]);
        var events = new List<DomainEvent>();
        await foreach (var ev in store.ReadAsync(stream))
            events.Add(ev.Ev);

        var roundTripped = Assert.IsType<ValueChanged>(Assert.Single(events));
        Assert.Equal(original.Value, roundTripped.Value);
        Assert.Equal(original.Metadata, roundTripped.Metadata);
    }

    /// <summary>
    /// Verifies schema evolution against real, persisted bytes rather than an in-memory JSON
    /// round trip: an event written by an old serializer configuration (only the old CLR type
    /// registered) is upcast correctly when a different serializer instance — only the new type
    /// and an upcaster registered, simulating a later deploy with the old type gone entirely —
    /// reads that same real Fitz stream back.
    /// </summary>
    [Fact]
    public async Task ShouldUpcastEventReadFromRealFitzStreamWrittenByOlderSchemaVersion()
    {
        await using var client = await _broker.CreateClientAsync();
        var aggregateId = Uuid.CreateVersion7();
        var stream = new EventStreamAddress("portia-integration", "event-store-evolution", aggregateId.ToString());

        var writerStore = new FitzEventStore(client.Stream, new JsonDomainEventSerializer(new DomainEventTypeCatalog().Register<WidgetNamed>()));
        var original = new WidgetNamed("Sprocket");
        original.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion7(), aggregateId, 1, DateTimeOffset.UtcNow));
        await writerStore.AppendAsync(stream, 0, [original]);

        var readerStore = new FitzEventStore(
            client.Stream,
            new JsonDomainEventSerializer(
                new DomainEventTypeCatalog().Register<WidgetRenamed>(),
                [new WidgetNamedToRenamedUpcaster()]));

        var events = new List<DomainEvent>();
        await foreach (var ev in readerStore.ReadAsync(stream))
            events.Add(ev.Ev);

        var widget = Assert.IsType<WidgetRenamed>(Assert.Single(events));
        Assert.Equal("Sprocket", widget.DisplayName);
    }

    /// <summary>
    /// Verifies that a stale writer against a real Fitz stream is rejected as
    /// <see cref="EventStreamConcurrencyException" /> — the same type
    /// <see cref="InMemoryEventStore" /> throws for the identical situation — rather than an
    /// unnormalized, Fitz-specific exception a caller has no stable way to catch and retry on.
    /// Retained as an acceptance gate: the pinned Fitz 0.1.1 client currently supplies no
    /// DomainCode for APPEND, so strict structured classification leaves this assertion failing
    /// until the upstream protocol/client carries code 2001. Do not reintroduce wording matching.
    /// </summary>
    [Fact]
    public async Task ShouldThrowConcurrencyExceptionWhenAppendingWithStaleExpectedVersion()
    {
        await using var client = await _broker.CreateClientAsync();
        var serializer = new JsonDomainEventSerializer(new DomainEventTypeCatalog().Register<ValueChanged>());
        var store = new FitzEventStore(client.Stream, serializer);
        var aggregateId = Uuid.CreateVersion7();
        var stream = new EventStreamAddress("portia-integration", "event-store-conflict", aggregateId.ToString());
        var first = new ValueChanged(1);
        first.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion7(), aggregateId, 1, DateTimeOffset.UtcNow));
        await store.AppendAsync(stream, 0, [first]);

        var stale = new ValueChanged(2);
        stale.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion7(), aggregateId, 1, DateTimeOffset.UtcNow));

        _ = await Assert.ThrowsAsync<EventStreamConcurrencyException>(() => store.AppendAsync(stream, 0, [stale]).AsTask());
    }

    /// <summary>
    /// Verifies that Portia's RPC sender and server exchange a typed request and result through
    /// two real Fitz client sessions.
    /// </summary>
    [Fact]
    public async Task ShouldRoundTripRequestThroughRealFitzBroker()
    {
        await using var workerClient = await _broker.CreateClientAsync();
        await using var callerClient = await _broker.CreateClientAsync();
        var serializer = new JsonRequestSerializer();
        using var busHost = TestRequestBus.Create();
        var server = new FitzRpcRequestServer(
            workerClient.Rpc,
            busHost.ScopeFactory);
        await using var registration = await server.RegisterAsync<RpcGetValue, int>();
        var sender = new FitzRemoteRequestSender(callerClient.Rpc, serializer, serializer);

        var result = await sender.SendAsync<RpcGetValue, int>(
            new RpcGetValue(),
            new RequestRouteValues(),
            actorToken: null);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
    }

    /// <summary>
    /// Verifies that calling a route with no registered worker fails fast and clearly, rather
    /// than hanging — confirming, against a real broker, that <see cref="FitzRemoteRequestSender" />
    /// deliberately imposing no timeout of its own (see its own remarks) is a safe choice: Fitz
    /// itself already fails a call to an unregistered route in milliseconds, not by hanging until
    /// some caller-supplied deadline.
    /// </summary>
    [Fact]
    public async Task ShouldFailFastWhenNoWorkerIsRegisteredForRoute()
    {
        await using var client = await _broker.CreateClientAsync();
        var serializer = new JsonRequestSerializer();
        var sender = new FitzRemoteRequestSender(client.Rpc, serializer, serializer);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        _ = await Assert.ThrowsAnyAsync<Exception>(() =>
            sender.SendAsync(new NoWorkerRegisteredPing(), new RequestRouteValues(), actorToken: null, cts.Token).AsTask());

        // Not just "it eventually throws before the 10s cap" — genuinely fast, proving this
        // isn't relying on the test's own cancellation to end the call.
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"Took {elapsed.Elapsed} — too close to looking like a hang.");
    }

    sealed class AlwaysValidActorValidator : IRequestActorValidator
    {
        public ValueTask<Result<System.Security.Claims.ClaimsPrincipal>> ValidateAsync(
            string? token,
            CancellationToken ct = default) =>
            ValueTask.FromResult(Result<System.Security.Claims.ClaimsPrincipal>.Success(RequestActor.System));
    }
}

// Deliberately never registered by any test — the whole point is a route no worker answers.
[RequestRoute(realm: "portia-integration", area: "rpc", resource: "no-worker-registered", operation: "ping")]
sealed record NoWorkerRegisteredPing : IRequest, ICallable;

sealed class NoWorkerRegisteredPingHandler : IRequestHandler<NoWorkerRegisteredPing>
{
    public ValueTask<Result> HandleAsync(IRequestContext<NoWorkerRegisteredPing> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}
