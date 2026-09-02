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
            events.Add(ev);

        var roundTripped = Assert.IsType<ValueChanged>(Assert.Single(events));
        Assert.Equal(original.Value, roundTripped.Value);
        Assert.Equal(original.Metadata, roundTripped.Metadata);
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
        var server = new FitzRpcRequestServer(
            workerClient.Rpc,
            serializer,
            TestRequestBus.Create(),
            new AlwaysValidActorValidator());
        await using var registration = await server.RegisterAsync<RpcGetValue, int>();
        var sender = new FitzRemoteRequestSender(callerClient.Rpc, serializer);

        var result = await sender.SendAsync<RpcGetValue, int>(
            new RpcGetValue(),
            new RequestRouteValues(),
            actorToken: null);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
    }

    sealed class AlwaysValidActorValidator : IRequestActorValidator
    {
        public ValueTask<Result<System.Security.Claims.ClaimsPrincipal>> ValidateAsync(
            string? token,
            CancellationToken ct = default) =>
            ValueTask.FromResult(Result<System.Security.Claims.ClaimsPrincipal>.Success(RequestActor.System));
    }
}
