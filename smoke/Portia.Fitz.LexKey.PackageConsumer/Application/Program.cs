using System.Text.Json;
using Cntryl.Fitz;
using Cntryl.Fitz.Testing;
using Cntryl.Keys;
using Cntryl.Portia;
using Microsoft.Extensions.DependencyInjection;

// Client-only composition supplies the built-in status contracts and JSON metadata. No application
// JSON context, status handler, analyzer package, worker activation, or broker connection is needed.
if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new InvalidOperationException("The packed processor status consumer must disable JSON reflection.");
var services = new ServiceCollection();
_ = services.AddPortia().AddFitz(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")));
await using (var provider = services.BuildServiceProvider())
{
    _ = provider.GetRequiredService<IProcessorStatusClient>();
    var options = provider.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json);
    var requestInfo = options.GetTypeInfo<GetProcessorStatus>();
    var request = new GetProcessorStatus("AccountBalanceProjector", "tenant-a");
    if (JsonSerializer.Deserialize(JsonSerializer.Serialize(request, requestInfo), requestInfo) != request)
        throw new InvalidOperationException("Packed processor status request metadata did not round-trip.");

    var statusInfo = options.GetTypeInfo<ProcessorStatus>();
    var status = new ProcessorStatus(request.Processor, "projector", request.Tenant,
        "stream://tenant-a/accounts/*", null,
        new EventStreamProgress(new EventCursor("opaque:committed"), new EventCursor("opaque:head"),
            4, false, DateTimeOffset.UnixEpoch),
        new ProcessorStatusFailure("Handling", "Conflict", true, 2, false, DateTimeOffset.UnixEpoch));
    var statusJson = JsonSerializer.Serialize(status, statusInfo);
    if (JsonSerializer.Deserialize(statusJson, statusInfo) != status)
        throw new InvalidOperationException("Packed processor status response metadata did not round-trip.");
    using var document = JsonDocument.Parse(statusJson);
    if (document.RootElement.GetProperty("progress").GetProperty("position").GetString() != "opaque:committed")
        throw new InvalidOperationException("Packed processor status metadata changed the opaque cursor representation.");
}

// A projection repository keyed by LexKey: Portia's packed Fitz projection store commits the
// application's LexKey-keyed write with the projector's checkpoint in one Fitz KV transaction, and a
// query-side read finds the row by the same key. Fitz's in-memory KV client stands in for the broker.
var client = new InMemoryKvClient();
var teams = new TeamRepository(client);
var identity = new CheckpointIdentity(TeamRepository.Projector, EventStreamPattern.ForPattern("consumer"));
var key = LexKey.EncodeComposite("tenant", 42L, "team");
var committed = new ProjectionCheckpoint(new EventCursor("1"));

await using (var batch = await teams.BeginAsync(new ProjectionBatchContext(identity, ProjectionCheckpoint.Start)))
{
    await teams.PutAsync(key, "value"u8.ToArray());
    await batch.CommitAsync(committed);
}

var value = await teams.GetAsync("consumer", key);
if (value is not { } found || !found.Span.SequenceEqual("value"u8))
{
    throw new InvalidOperationException("A LexKey-keyed projection row did not round-trip through the packed Fitz store.");
}

if (await teams.LoadCheckpointAsync(identity) != committed)
{
    throw new InvalidOperationException("The projection checkpoint did not commit with the LexKey-keyed row.");
}

Console.WriteLine("Fitz LexKey and processor status package consumer passed.");

sealed class TeamRepository(IKvClient kv) : FitzKvProjectionStore(kv, "kv://consumer/package/teams", Projector)
{
    public const string Projector = "TeamProjector";

    // LexKey's allocation-free memory view is the Fitz key as it is; neither Portia nor Fitz re-encodes it.
    public Task PutAsync(LexKey key, ReadOnlyMemory<byte> value) => Transaction.PutAsync(key.AsMemory(), value);

    public async ValueTask<ReadOnlyMemory<byte>?> GetAsync(string realm, LexKey key)
    {
        await using var transaction = await BeginReadAsync(realm);
        var result = await transaction.GetAsync(key.AsMemory());
        return result is KvFound { Value: var bytes } ? bytes : null;
    }
}
