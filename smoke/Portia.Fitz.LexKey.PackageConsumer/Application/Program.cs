using Cntryl.Fitz;
using Cntryl.Fitz.Testing;
using Cntryl.Keys;
using Cntryl.Portia;

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

Console.WriteLine("Fitz LexKey package consumer passed.");

sealed class TeamRepository(IKvClient kv) : FitzKvProjectionStore(kv, "kv://consumer/package/teams", Projector)
{
    public const string Projector = "TeamProjector";

    // LexKey's allocation-free memory view is the Fitz key as it is; neither Portia nor Fitz re-encodes it.
    public Task PutAsync(LexKey key, ReadOnlyMemory<byte> value) => Transaction.PutAsync(key.AsMemory(), value);

    public async ValueTask<ReadOnlyMemory<byte>?> GetAsync(string realm, LexKey key)
    {
        await using var transaction = await BeginReadAsync(realm);
        var result = await transaction.GetAsync(key.AsMemory());
        return result.Found ? result.Value : null;
    }
}
