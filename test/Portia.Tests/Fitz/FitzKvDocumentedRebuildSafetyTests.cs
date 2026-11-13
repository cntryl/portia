using System.Buffers.Binary;
using System.Text;

namespace Cntryl.Portia;

/// <summary>Verifies generation safety for the documented balance repository example.</summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
[Trait("Category", "BrokerIntegration")]
public sealed class FitzKvDocumentedRebuildSafetyProof(FitzBrokerFixture broker)
{
    /// <summary>An isolated candidate replay must not apply its delta to the currently queried live value.</summary>
    [Fact]
    public async Task ShouldKeepLiveBalanceUnchangedGivenCandidateReplay()
    {
        // Arrange
        await using var client = await broker.CreateClientAsync();
        var realm = "review-" + Uuid.CreateVersion4();
        var repository = new DocumentedBalanceRepository(client.Kv, "kv://portia-review/rebuild/" + Uuid.CreateVersion4());
        var pattern = EventStreamPattern.ForPattern(realm, "orders");
        var live = new CheckpointIdentity("AccountProjector", pattern);
        var candidate = new CheckpointIdentity("AccountProjector", pattern, "candidate");
        var account = Uuid.CreateVersion4();
        var checkpoint = new ProjectionCheckpoint(new EventCursor("1"));

        await using (var batch = await repository.BeginAsync(new ProjectionBatchContext(live, ProjectionCheckpoint.Start)))
        {
            await repository.IncrementBalanceAsync(account, 10, live.RebuildId);
            await batch.CommitAsync(checkpoint);
        }
        Assert.Equal(10L, await repository.ReadBalanceAsync(realm, account));

        // Act
        await using (var batch = await repository.BeginAsync(new ProjectionBatchContext(candidate, ProjectionCheckpoint.Start)))
        {
            await repository.IncrementBalanceAsync(account, 10, candidate.RebuildId);
            await batch.CommitAsync(checkpoint);
        }

        // Assert
        Assert.Equal(checkpoint, await repository.LoadCheckpointAsync(live));
        Assert.Equal(checkpoint, await repository.LoadCheckpointAsync(candidate));
        Assert.Equal(repository.WriteRoutes[0], repository.WriteRoutes[1]);
        Assert.Equal(10L, await repository.ReadBalanceAsync(realm, account));
        Assert.Equal(10L, await repository.ReadBalanceAsync(realm, account, candidate.RebuildId));
    }

    sealed class DocumentedBalanceRepository(IKvClient client, string route)
        : FitzKvProjectionStore(client, route, "AccountProjector")
    {
        internal List<string> WriteRoutes { get; } = [];

        // The key and delta calculation intentionally follow docs/projectors-and-reactors.md.
        internal async Task IncrementBalanceAsync(Uuid accountId, int amount, string? rebuildId)
        {
            WriteRoutes.Add(Transaction.Route);
            var key = BalanceKey(accountId, rebuildId);
            var current = await Transaction.GetAsync(key);
            var balance = current is KvFound { Value: var currentBytes } ? BinaryPrimitives.ReadInt64BigEndian(currentBytes.Span) : 0;
            var next = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(next, balance + amount);
            await Transaction.PutAsync(key, next);
        }

        internal async Task<long?> ReadBalanceAsync(string realm, Uuid accountId, string? rebuildId = null)
        {
            await using var transaction = await BeginReadAsync(realm);
            var current = await transaction.GetAsync(BalanceKey(accountId, rebuildId));
            return current is KvFound { Value: var currentBytes } ? BinaryPrimitives.ReadInt64BigEndian(currentBytes.Span) : null;
        }

        static byte[] BalanceKey(Uuid accountId, string? rebuildId) => Encoding.UTF8.GetBytes(
            rebuildId is null ? $"balance\0{accountId}" : $"balance-rebuild\0{rebuildId}\0{accountId}");
    }
}
