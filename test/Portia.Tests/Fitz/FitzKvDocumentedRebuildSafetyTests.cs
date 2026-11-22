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
        var repository =
            new DocumentedBalanceRepository(client.Kv, "kv://portia-review/rebuild/" + Uuid.CreateVersion4());
        var pattern = EventStreamPattern.ForPattern(realm, "orders");
        var live = new CheckpointIdentity("AccountProjector", pattern);
        var candidate = new CheckpointIdentity("AccountProjector", pattern, "candidate");
        var account = Uuid.CreateVersion4();
        var checkpoint = new ProjectionCheckpoint(new EventCursor("1"));

        await using (var batch =
                     await repository.BeginAsync(new ProjectionBatchContext(live, ProjectionCheckpoint.Start), TestContext.Current.CancellationToken))
        {
            await repository.IncrementBalanceAsync(account, 625);
            await batch.CommitAsync(checkpoint, TestContext.Current.CancellationToken);
        }

        Assert.Equal(625L, await repository.ReadBalanceAsync(live, account));

        // Act
        await using (var batch =
                     await repository.BeginAsync(new ProjectionBatchContext(candidate, ProjectionCheckpoint.Start), TestContext.Current.CancellationToken))
        {
            await repository.IncrementBalanceAsync(account, 500);
            await batch.CommitAsync(checkpoint, TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.Equal(checkpoint, await repository.LoadCheckpointAsync(live, TestContext.Current.CancellationToken));
        Assert.Equal(checkpoint, await repository.LoadCheckpointAsync(candidate, TestContext.Current.CancellationToken));
        Assert.NotEqual(repository.WriteRoutes[0], repository.WriteRoutes[1]);
        Assert.Equal(625L, await repository.ReadBalanceAsync(live, account));
        Assert.Equal(500L, await repository.ReadBalanceAsync(candidate, account));
    }

    /// <summary>Candidate scans and deletes use the candidate transaction and reject concurrent writers.</summary>
    [Fact]
    public async Task ShouldFenceCandidateWriterGivenNativeScanAndDelete()
    {
        // Arrange
        await using var client = await broker.CreateClientAsync();
        var route = "kv://portia-review/rebuild/" + Uuid.CreateVersion4();
        var repository = new DocumentedBalanceRepository(client.Kv, route);
        var contender = new DocumentedBalanceRepository(client.Kv, route);
        var pattern = EventStreamPattern.ForPattern("tenant", "accounts");
        var live = new CheckpointIdentity("AccountProjector", pattern);
        var candidate = new CheckpointIdentity("AccountProjector", pattern, "candidate");
        var account = Uuid.CreateVersion4();
        var first = new ProjectionCheckpoint(new EventCursor("1"));
        foreach (var identity in new[] { live, candidate })
        {
            await using var seed = await repository.BeginAsync(new ProjectionBatchContext(identity, ProjectionCheckpoint.Start), TestContext.Current.CancellationToken);
            await repository.IncrementBalanceAsync(account, 1);
            await seed.CommitAsync(first, TestContext.Current.CancellationToken);
        }

        // Act
        var context = new ProjectionBatchContext(candidate, first);
        await using var batch = await repository.BeginAsync(context, TestContext.Current.CancellationToken);
        var conflict = await Record.ExceptionAsync(async () => await contender.BeginAsync(context, TestContext.Current.CancellationToken));
        var removed = await repository.DeleteBalancesAsync();
        await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("2")), TestContext.Current.CancellationToken);

        // Assert
        Assert.IsType<ProjectionConcurrencyException>(conflict);
        Assert.Equal(1, removed);
        Assert.Null(await contender.ReadBalanceAsync(candidate, account));
        Assert.Equal(1L, await contender.ReadBalanceAsync(live, account));
    }

    sealed class DocumentedBalanceRepository(IKvClient client, string route)
        : FitzKvProjectionStore(client, route, "AccountProjector")
    {
        internal List<string> WriteRoutes { get; } = [];

        // The key and delta calculation intentionally follow docs/projectors-and-reactors.md.
        internal async Task IncrementBalanceAsync(Uuid accountId, int amount)
        {
            WriteRoutes.Add(Transaction.Route);
            var key = BalanceKey(accountId);
            var current = await Transaction.GetAsync(key);
            var balance = current is KvFound { Value: var currentBytes }
                ? BinaryPrimitives.ReadInt64BigEndian(currentBytes.Span)
                : 0;
            var next = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(next, balance + amount);
            await Transaction.PutAsync(key, next);
        }

        internal async Task<int> DeleteBalancesAsync()
        {
            var page = await Transaction.ScanAsync(new KvScanQuery("balance"u8.ToArray(), "balancf"u8.ToArray()));
            foreach (var pair in page.Pairs)
                await Transaction.DeleteAsync(pair.Key);
            return page.Pairs.Count;
        }

        internal async Task<long?> ReadBalanceAsync(CheckpointIdentity identity, Uuid accountId)
        {
            await using var transaction = await BeginReadAsync(identity, default);
            var current = await transaction.GetAsync(BalanceKey(accountId));
            return current is KvFound { Value: var currentBytes }
                ? BinaryPrimitives.ReadInt64BigEndian(currentBytes.Span)
                : null;
        }

        static byte[] BalanceKey(Uuid accountId) => Encoding.UTF8.GetBytes(
            $"balance\0{accountId}");
    }
}
