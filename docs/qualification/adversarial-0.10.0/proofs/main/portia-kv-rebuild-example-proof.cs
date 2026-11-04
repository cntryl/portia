using System.Buffers.Binary;
using System.Text;

namespace Cntryl.Portia;

/// <summary>Adversarial proof of generation safety for the documented unversioned balance example.</summary>
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
            await repository.IncrementBalanceAsync(account, 10);
            await batch.CommitAsync(checkpoint);
        }
        Assert.Equal(10L, await repository.ReadBalanceAsync(realm, account));

        // Act
        await using (var batch = await repository.BeginAsync(new ProjectionBatchContext(candidate, ProjectionCheckpoint.Start)))
        {
            await repository.IncrementBalanceAsync(account, 10);
            await batch.CommitAsync(checkpoint);
        }

        // Assert
        Assert.Equal(checkpoint, await repository.LoadCheckpointAsync(live));
        Assert.Equal(checkpoint, await repository.LoadCheckpointAsync(candidate));
        Assert.Equal(repository.WriteRoutes[0], repository.WriteRoutes[1]);
        Assert.Equal(10L, await repository.ReadBalanceAsync(realm, account));
    }

    sealed class DocumentedBalanceRepository(IKvClient client, string route)
        : FitzKvProjectionStore(client, route, "AccountProjector")
    {
        internal List<string> WriteRoutes { get; } = [];

        // The key and delta calculation intentionally follow docs/projectors-and-reactors.md.
        internal async Task IncrementBalanceAsync(Uuid accountId, int amount)
        {
            WriteRoutes.Add(Transaction.Route);
            var key = Encoding.UTF8.GetBytes($"balance\0{accountId}");
            var current = await Transaction.GetAsync(key);
            var balance = current.Found ? BinaryPrimitives.ReadInt64BigEndian(current.Value!.Value.Span) : 0;
            var next = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(next, balance + amount);
            await Transaction.PutAsync(key, next);
        }

        internal async Task<long?> ReadBalanceAsync(string realm, Uuid accountId)
        {
            await using var transaction = await BeginReadAsync(realm);
            var current = await transaction.GetAsync(Encoding.UTF8.GetBytes($"balance\0{accountId}"));
            return current.Found ? BinaryPrimitives.ReadInt64BigEndian(current.Value!.Value.Span) : null;
        }
    }
}
