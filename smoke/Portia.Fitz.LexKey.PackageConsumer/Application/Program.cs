using Cntryl.Fitz;
using Cntryl.Fitz.Testing;
using Cntryl.Keys;
using Cntryl.Portia;

const string route = "kv://consumer/package/keys";
var key = LexKey.EncodeComposite("tenant", 42L, "team");
var client = new InMemoryKvClient();
client.Seed(route, key.AsMemory(), "value"u8.ToArray());

var result = await ReadAsync(client, route, key.AsMemory());

if (!result.Found || !result.Value!.Value.Span.SequenceEqual("value"u8))
{
    throw new InvalidOperationException("LexKey memory did not round-trip through the packed Fitz test client.");
}

Console.WriteLine(typeof(FitzEventStore).FullName);

static async Task<KvGetResult> ReadAsync(
    IKvClient client,
    string route,
    ReadOnlyMemory<byte> key)
{
    await using var transaction = await client.BeginAsync(
        route,
        KvDurability.Async,
        KvMode.ReadOnly);
    return await transaction.GetAsync(key);
}
