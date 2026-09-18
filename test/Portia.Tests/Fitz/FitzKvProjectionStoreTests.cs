using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Cntryl.Portia;

/// <summary>
///     Verifies the Fitz KV projection store: that a derived repository's own domain writes and the
///     checkpoint Portia advances land in one atomic Fitz KV transaction, and that a batch which does
///     not commit leaves neither behind.
/// </summary>
public sealed class FitzKvProjectionStoreTests
{
    static readonly CheckpointIdentity Identity = new("orders", EventStreamPattern.ForPattern("tenant", "orders"));

    /// <summary>Failure stages crossed with healthy and failing cleanup paths.</summary>
    public static TheoryData<string, bool> BeginValidationFailures => new()
    {
        { "read", false },
        { "read", true },
        { "cancellation", false },
        { "cancellation", true },
        { "decode", false },
        { "decode", true },
        { "stale", false },
        { "stale", true }
    };

    /// <summary>
    ///     Every failure after BEGIN must relinquish the transaction, preserve the validation failure,
    ///     and leave the store reusable even when rollback and disposal themselves fail.
    /// </summary>
    [Theory]
    [MemberData(nameof(BeginValidationFailures))]
    public async Task ShouldCleanUpAndRemainReusableWhenBeginValidationFails(string failureKind,
        bool cleanupFails)
    {
        var original = new IOException("checkpoint read failed");
        using var cancellation = new CancellationTokenSource();
        var client = new FakeKvClient();
        var repository = new TotalsRepository(client, "kv://portia/state/orders");
        var supplied = ProjectionCheckpoint.Start;
        var key = FitzKvCheckpoints.Key(Identity);
        switch (failureKind)
        {
            case "read":
                client.GetFailure = original;
                break;
            case "cancellation":
                client.CancelDuringGet = cancellation;
                break;
            case "decode":
                client.Committed[Encoding.UTF8.GetString(key.Span)] = [1, 2, 3];
                break;
            case "stale":
                client.Committed[Encoding.UTF8.GetString(key.Span)] =
                    FitzKvCheckpoints.Encode(new EventCursor("authoritative")).ToArray();
                break;
            default:
                throw new InvalidOperationException($"Unknown failure kind '{failureKind}'.");
        }

        var checkpointBeforeFailure = client.Read(key)?.ToArray();

        if (cleanupFails)
        {
            client.RollbackFailure = new IOException("rollback failed");
            client.DisposeFailure = new IOException("dispose failed");
        }

        Exception error;
        if (failureKind == "read")
        {
            error = await Assert.ThrowsAsync<IOException>(async () =>
                await repository.BeginAsync(new ProjectionBatchContext(Identity, supplied)));
            Assert.Same(original, error);
        }
        else if (failureKind == "cancellation")
        {
            error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await repository.BeginAsync(new ProjectionBatchContext(Identity, supplied), cancellation.Token));
            Assert.Equal(cancellation.Token, ((OperationCanceledException)error).CancellationToken);
        }
        else if (failureKind == "decode")
        {
            error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await repository.BeginAsync(new ProjectionBatchContext(Identity, supplied)));
        }
        else
        {
            error = await Assert.ThrowsAsync<ProjectionConcurrencyException>(async () =>
                await repository.BeginAsync(new ProjectionBatchContext(Identity, supplied)));
        }

        var failed = client.LastTransaction;
        Assert.Equal(1, failed.Rollbacks);
        Assert.Equal(1, failed.Disposals);
        Assert.Equal(0, failed.Commits);
        Assert.False(Assert.Single(client.RollbackTokens).CanBeCanceled);
        Assert.Equal(checkpointBeforeFailure, client.Read(key));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddAsync("outside", 1));
        Assert.Null(client.Read(TotalsRepository.DataKey("outside")));

        client.GetFailure = null;
        client.CancelDuringGet = null;
        client.RollbackFailure = null;
        client.DisposeFailure = null;
        client.Committed.Remove(Encoding.UTF8.GetString(key.Span));
        await using (var batch = await repository.BeginAsync(
                         new ProjectionBatchContext(Identity, ProjectionCheckpoint.Start)))
        {
            await repository.AddAsync("total", 12);
            await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("reused")));
        }

        Assert.Equal("12", Encoding.UTF8.GetString(client.Read(TotalsRepository.DataKey("total"))!));
        Assert.Equal(2, client.Transactions.Count);
    }

    /// <summary>A committed reset can be loaded and used to begin the next batch.</summary>
    [Fact]
    public async Task ShouldReloadStartAfterResetBatch()
    {
        var client = new FakeKvClient();
        const string route = "kv://portia/state/orders";
        var store = new TotalsRepository(client, route);
        var progress = new ProjectionCheckpoint(new EventCursor("progress"));
        await using (var batch =
                     await store.BeginAsync(new ProjectionBatchContext(Identity, ProjectionCheckpoint.Start)))
            await batch.CommitAsync(progress);
        await using (var batch = await store.BeginAsync(new ProjectionBatchContext(Identity, progress)))
            await batch.CommitAsync(ProjectionCheckpoint.Start);
        Assert.Equal(ProjectionCheckpoint.Start, await new FitzKvCheckpointStore(client, route).LoadAsync(Identity));
        var fresh = new TotalsRepository(client, route);
        Assert.Equal(ProjectionCheckpoint.Start, await fresh.LoadCheckpointAsync(Identity));
        await using (var batch =
                     await fresh.BeginAsync(new ProjectionBatchContext(Identity, ProjectionCheckpoint.Start)))
            await batch.CommitAsync(progress);
        Assert.Equal(progress, await fresh.LoadCheckpointAsync(Identity));
    }

    /// <summary>
    ///     Verifies the whole point of sharing a transaction: the repository's domain write and the
    ///     checkpoint become visible together, in one commit, and never separately.
    /// </summary>
    [Fact]
    public async Task ShouldCommitRepositoryWritesAndCheckpointInOneTransaction()
    {
        var client = new FakeKvClient();
        var store = new TotalsRepository(client, "kv://portia/state/orders");

        await using (var batch =
                     await store.BeginAsync(new ProjectionBatchContext(Identity, ProjectionCheckpoint.Start)))
        {
            await store.AddAsync("total", 12);
            Assert.Empty(client.Committed);
            await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("9")));
        }

        Assert.Equal(1, client.LastTransaction.Commits);
        Assert.Equal("12", Encoding.UTF8.GetString(client.Read(TotalsRepository.DataKey("total"))!));
        Assert.Equal(new ProjectionCheckpoint(new EventCursor("9")), await store.LoadCheckpointAsync(Identity));
    }

    /// <summary>Accepts 0.1.x progress at begin and upgrades it only when the next batch commits.</summary>
    [Fact]
    public async Task ShouldAcceptLegacyCheckpointAndRewriteItOnCommit()
    {
        var client = new FakeKvClient();
        var legacy = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(legacy, 8);
        var key = FitzKvCheckpoints.Key(Identity);
        client.Committed[Encoding.UTF8.GetString(key.Span)] = legacy;
        var store = new TotalsRepository(client, "kv://portia/state/orders");

        await using (var batch = await store.BeginAsync(new ProjectionBatchContext(
                         Identity, new ProjectionCheckpoint(new EventCursor("8")))))
            await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("opaque-next")));

        Assert.Equal(new ProjectionCheckpoint(new EventCursor("opaque-next")),
            await store.LoadCheckpointAsync(Identity));
        Assert.Equal("portia-checkpoint-v1\0opaque-next", Encoding.UTF8.GetString(client.Read(key)!));
    }

    /// <summary>
    ///     Verifies that a batch disposed without committing rolls its transaction back, so a pass that
    ///     faults midway leaves neither half-applied projection data nor an advanced checkpoint.
    /// </summary>
    [Fact]
    public async Task ShouldRollBackWhenBatchIsDisposedWithoutCommitting()
    {
        var client = new FakeKvClient();
        var store = new TotalsRepository(client, "kv://portia/state/orders");

        await using (await store.BeginAsync(new ProjectionBatchContext(Identity, ProjectionCheckpoint.Start)))
            await store.AddAsync("total", 12);

        Assert.Equal(1, client.LastTransaction.Rollbacks);
        Assert.Equal(0, client.LastTransaction.Commits);
        Assert.Equal(1, client.LastTransaction.Disposals);
        Assert.Empty(client.Committed);
        Assert.Equal(ProjectionCheckpoint.Start, await store.LoadCheckpointAsync(Identity));
    }

    /// <summary>
    ///     Verifies that disposing a committed batch does not roll the transaction back a second time —
    ///     the committed work stays committed and the broker sees one terminal call, not two.
    /// </summary>
    [Fact]
    public async Task ShouldNotRollBackAfterACommittedBatchIsDisposed()
    {
        var client = new FakeKvClient();
        var store = new TotalsRepository(client, "kv://portia/state/orders");

        await using (var batch =
                     await store.BeginAsync(new ProjectionBatchContext(Identity, ProjectionCheckpoint.Start)))
            await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("1")));

        Assert.Equal(0, client.LastTransaction.Rollbacks);
        Assert.Equal(1, client.LastTransaction.Commits);
        Assert.Equal(1, client.LastTransaction.Disposals);
    }

    /// <summary>
    ///     Verifies that a Fitz KV isolation conflict becomes <see cref="ProjectionConcurrencyException" />
    ///     naming the projection, so the hosted service knows to reload the authoritative checkpoint
    ///     instead of replaying from its stale in-memory one.
    /// </summary>
    [Fact]
    public async Task ShouldTranslateIsolationConflictToProjectionConcurrencyException()
    {
        var conflict = new KvException("conflict", "TX_CONFLICT", domainCode: FitzErrorCodes.KvIsolationConflict);
        var client = new FakeKvClient { CommitFailure = conflict };
        var store = new TotalsRepository(client, "kv://portia/state/orders");
        await using var batch =
            await store.BeginAsync(new ProjectionBatchContext(Identity, ProjectionCheckpoint.Start));
        await store.AddAsync("total", 12);

        var error = await Assert.ThrowsAsync<ProjectionConcurrencyException>(async () =>
            await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("1"))));

        Assert.Same(conflict, error.InnerException);
        Assert.Contains("orders", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, client.LastTransaction.Rollbacks);
        Assert.Empty(client.Committed);
    }

    /// <summary>
    ///     Verifies that any other commit failure reaches the caller unchanged, and that disposing the
    ///     failed batch afterwards does not roll back a second time.
    /// </summary>
    [Fact]
    public async Task ShouldRethrowNonConflictCommitFailureAndRollBackOnce()
    {
        var failure = new KvException("backend down", "KV_BACKEND", domainCode: FitzErrorCodes.KvBackendError);
        var client = new FakeKvClient { CommitFailure = failure };
        var store = new TotalsRepository(client, "kv://portia/state/orders");
        var batch = await store.BeginAsync(new ProjectionBatchContext(Identity, ProjectionCheckpoint.Start));

        var error = await Assert.ThrowsAsync<KvException>(async () =>
            await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("1"))));
        await batch.DisposeAsync();

        Assert.Same(failure, error);
        Assert.Equal(1, client.LastTransaction.Rollbacks);
        Assert.Equal(1, client.LastTransaction.Disposals);
    }

    /// <summary>
    ///     Verifies that a rollback which itself fails after a failed commit is swallowed, leaving the
    ///     commit failure as the exception the caller sees.
    /// </summary>
    [Fact]
    public async Task ShouldPreserveCommitFailureWhenRollbackAlsoFails()
    {
        var failure = new KvException("commit failed", "KV_BACKEND", domainCode: FitzErrorCodes.KvBackendError);
        var client = new FakeKvClient { CommitFailure = failure, RollbackFailure = new IOException("rollback failed") };
        var store = new TotalsRepository(client, "kv://portia/state/orders");
        await using var batch =
            await store.BeginAsync(new ProjectionBatchContext(Identity, ProjectionCheckpoint.Start));

        var error = await Assert.ThrowsAsync<KvException>(async () =>
            await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("1"))));

        Assert.Same(failure, error);
    }

    /// <summary>
    ///     Verifies that a component which has never checkpointed resumes from the start of its pattern,
    ///     read through its own read-only transaction rather than the batch's.
    /// </summary>
    [Fact]
    public async Task ShouldLoadStartWhenNothingHasBeenSaved()
    {
        var client = new FakeKvClient();

        var checkpoint = await new TotalsRepository(client, "kv://portia/state/orders").LoadCheckpointAsync(Identity);

        Assert.Equal(ProjectionCheckpoint.Start, checkpoint);
        Assert.Equal(KvMode.ReadOnly, client.LastTransaction.Mode);
    }

    /// <summary>
    ///     Verifies that a rebuild generation commits to its own keys, leaving the live generation's
    ///     checkpoint untouched so a rebuild never repoints the deployment that is still serving reads.
    /// </summary>
    [Fact]
    public async Task ShouldCommitRebuildGenerationWithoutDisturbingLiveCheckpoint()
    {
        var client = new FakeKvClient();
        var store = new TotalsRepository(client, "kv://portia/state/orders");
        var rebuild = new CheckpointIdentity(
            Identity.ComponentName, EventStreamPattern.ForPattern("tenant", "orders"), "rebuild-1");
        await using (var live = await store.BeginAsync(new ProjectionBatchContext(Identity,
                         ProjectionCheckpoint.Start)))
            await live.CommitAsync(new ProjectionCheckpoint(new EventCursor("2")));

        await using (var batch =
                     await store.BeginAsync(new ProjectionBatchContext(rebuild, ProjectionCheckpoint.Start)))
            await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("7")));

        Assert.Equal(new ProjectionCheckpoint(new EventCursor("2")), await store.LoadCheckpointAsync(Identity));
        Assert.Equal(new ProjectionCheckpoint(new EventCursor("7")), await store.LoadCheckpointAsync(rebuild));
    }

    /// <summary>
    ///     Verifies that beginning a second batch while one is still open fails fast. The store hands
    ///     derived repositories one shared transaction, so a second begin would silently retarget the
    ///     open batch's writes and commit — or roll back — the wrong transaction.
    /// </summary>
    [Fact]
    public async Task ShouldRejectASecondBatchWhileOneIsOpen()
    {
        var client = new FakeKvClient();
        var store = new TotalsRepository(client, "kv://portia/state/orders");
        await using var batch =
            await store.BeginAsync(new ProjectionBatchContext(Identity, ProjectionCheckpoint.Start));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.BeginAsync(new ProjectionBatchContext(Identity, ProjectionCheckpoint.Start)));

        Assert.Contains("already open", error.Message, StringComparison.OrdinalIgnoreCase);
        _ = Assert.Single(client.Transactions);
    }

    /// <summary>
    ///     Verifies that the store is usable again once its batch is disposed — the single-batch rule
    ///     bounds concurrency, it does not permanently retire the store after one pass.
    /// </summary>
    [Fact]
    public async Task ShouldAcceptAnotherBatchAfterThePreviousOneIsDisposed()
    {
        var client = new FakeKvClient();
        var store = new TotalsRepository(client, "kv://portia/state/orders");
        await using (var first =
                     await store.BeginAsync(new ProjectionBatchContext(Identity, ProjectionCheckpoint.Start)))
            await first.CommitAsync(new ProjectionCheckpoint(new EventCursor("1")));

        await using (var second =
                     await store.BeginAsync(new ProjectionBatchContext(Identity,
                         new ProjectionCheckpoint(new EventCursor("1")))))
            await second.CommitAsync(new ProjectionCheckpoint(new EventCursor("2")));

        Assert.Equal(2, client.Transactions.Count);
        Assert.Equal(new ProjectionCheckpoint(new EventCursor("2")), await store.LoadCheckpointAsync(Identity));
    }

    /// <summary>
    ///     Verifies that a missing client or a route that is not shaped
    ///     <c>kv://{realm}/{area}/{resource}</c> is rejected at construction, rather than accepted and
    ///     then rejected by the broker on every single read and write for the life of the process.
    /// </summary>
    /// <param name="route">The route to construct with, or <see langword="null" /> to omit the client instead.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("portia/state/checkpoints")]
    [InlineData("kv://portia/state")]
    [InlineData("kv://portia/state/checkpoints/extra")]
    [InlineData("kv://portia//checkpoints")]
    [InlineData("kv://portia/state/*")]
    [InlineData("kv://portia/state/check points")]
    public void ShouldRejectMissingClientOrMalformedRoute(string? route)
    {
        if (route is null)
        {
            _ = Assert.Throws<ArgumentNullException>(() => new TotalsRepository(null!, "kv://portia/state/orders"));
        }
        else
        {
            _ = Assert.Throws<ArgumentException>(() => new TotalsRepository(new FakeKvClient(), route));
        }
    }

    /// <summary>
    ///     Verifies that a missing identity or batch context is rejected before a transaction is opened.
    /// </summary>
    /// <param name="beginning"><see langword="true" /> to check the begin path; otherwise the load path.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectMissingArgumentBeforeOpeningATransaction(bool beginning)
    {
        var client = new FakeKvClient();
        var store = new TotalsRepository(client, "kv://portia/state/orders");

        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            if (beginning)
            {
                _ = await store.BeginAsync(null!);
            }
            else
            {
                _ = await store.LoadCheckpointAsync(null!);
            }
        });

        Assert.Empty(client.Transactions);
    }

    /// <summary>
    ///     Verifies that the configured route is a base, not one shared resource: each tenant of a
    ///     per-tenant projector, and each projector sharing a base route, transacts against its own
    ///     derived Fitz KV resource, so none of them can contend for another's lock.
    /// </summary>
    [Fact]
    public async Task ShouldRouteEachWorkloadToItsOwnFitzKvResource()
    {
        var client = new FakeKvClient();
        CheckpointIdentity[] workloads =
        [
            new("orders", EventStreamPattern.ForPattern("tenant-a", "orders")),
            new("orders", EventStreamPattern.ForPattern("tenant-b", "orders")),
            new("invoices", EventStreamPattern.ForPattern("tenant-a", "orders"))
        ];

        foreach (var workload in workloads)
        {
            await using var batch = await new TotalsRepository(client, "kv://portia/state/orders")
                .BeginAsync(new ProjectionBatchContext(workload, ProjectionCheckpoint.Start));
            await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("1")));
        }

        var routes = client.Transactions.Select(tx => tx.Route).Distinct().ToArray();
        Assert.Equal(workloads.Length, routes.Length);
        Assert.All(routes, route => Assert.StartsWith("kv://portia/state/orders-", route, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Verifies that one tenant's open projection batch never blocks another tenant's batch for the
    ///     same projector against a broker that locks a whole resource per read-write transaction.
    /// </summary>
    [Fact]
    public async Task ShouldBeginOneTenantsBatchWhileAnotherTenantsBatchIsOpen()
    {
        var client = new FakeKvClient();
        var tenantA = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("tenant-a", "orders"));
        var tenantB = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("tenant-b", "orders"));
        await using var open = await new TotalsRepository(client, "kv://portia/state/orders")
            .BeginAsync(new ProjectionBatchContext(tenantA, ProjectionCheckpoint.Start));

        await using var other = await new TotalsRepository(client, "kv://portia/state/orders")
            .BeginAsync(new ProjectionBatchContext(tenantB, ProjectionCheckpoint.Start));
        await other.CommitAsync(new ProjectionCheckpoint(new EventCursor("1")));
    }

    /// <summary>
    ///     Verifies that the published route helper names exactly the resource a batch and a checkpoint
    ///     load use, so an application's query-side reads find the data its projector wrote.
    /// </summary>
    [Fact]
    public async Task ShouldExposeTheRouteABatchAndCheckpointLoadUse()
    {
        var client = new FakeKvClient();
        var store = new TotalsRepository(client, "kv://portia/state/orders");
        var identity = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("tenant-a", "orders"));

        await using (var batch = await store.BeginAsync(new ProjectionBatchContext(identity, ProjectionCheckpoint.Start)))
            await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("1")));
        _ = await store.LoadCheckpointAsync(identity);

        var expected = FitzKvProjectionStore.RouteFor("kv://portia/state/orders", "orders", "tenant-a");
        Assert.All(client.Transactions, tx => Assert.Equal(expected, tx.Route));
    }

    // A minimal derived repository: it writes its own domain keys through the shared transaction,
    // exactly as a real projection repository is meant to.
    sealed class TotalsRepository(IKvClient client, string route) : FitzKvProjectionStore(client, route)
    {
        public static ReadOnlyMemory<byte> DataKey(string name) => Encoding.UTF8.GetBytes("total\0" + name);

        public Task AddAsync(string name, int amount) =>
            Transaction.PutAsync(DataKey(name),
                Encoding.UTF8.GetBytes(amount.ToString(CultureInfo.InvariantCulture)));
    }
}
