using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Cntryl.Portia;

/// <summary>
///     Verifies the durable reactor checkpoint store backed by Fitz KV: what it writes, what it reads
///     back, and — the part a reactor's exactly-once story depends on — what it does when the write
///     fails partway through.
/// </summary>
public sealed class FitzKvCheckpointStoreTests
{
    static readonly CheckpointIdentity Identity = new("reactor", EventStreamPattern.ForPattern("tenant", "orders"));

    /// <summary>A persisted reset survives a new reactor store instance.</summary>
    [Fact]
    public async Task ShouldReloadStartAfterReset()
    {
        var client = new FakeKvClient();
        var store = new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints");
        await store.SaveAsync(Identity, new ProjectionCheckpoint(new EventCursor("progress")));
        await store.SaveAsync(Identity, ProjectionCheckpoint.Start);
        Assert.Equal(ProjectionCheckpoint.Start,
            await new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints").LoadAsync(Identity));
        Assert.Equal("portia-checkpoint-v1\0"u8.ToArray(), Assert.Single(client.Committed).Value);
    }

    /// <summary>
    ///     Verifies that a component which has never checkpointed resumes from the start of its pattern
    ///     rather than reporting whatever a missing key decodes to.
    /// </summary>
    [Fact]
    public async Task ShouldLoadStartWhenNothingHasBeenSaved()
    {
        var client = new FakeKvClient();

        var checkpoint = await new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints").LoadAsync(Identity);

        Assert.Equal(ProjectionCheckpoint.Start, checkpoint);
        Assert.Equal(KvMode.ReadOnly, client.LastTransaction.Mode);
        Assert.Equal(1, client.LastTransaction.Disposals);
    }

    /// <summary>
    ///     Verifies that a saved cursor reads back unchanged, stored with the versioned prefix
    ///     that distinguishes opaque cursors from legacy big-endian offsets.
    /// </summary>
    [Fact]
    public async Task ShouldRoundTripSavedCheckpointInVersionedFormat()
    {
        var client = new FakeKvClient();
        var store = new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints");

        await store.SaveAsync(Identity, new ProjectionCheckpoint(new EventCursor("4096")));

        Assert.Equal(new ProjectionCheckpoint(new EventCursor("4096")), await store.LoadAsync(Identity));
        var stored = Assert.Single(client.Committed).Value;
        Assert.Equal("portia-checkpoint-v1\0"u8.ToArray().Concat("4096"u8.ToArray()), stored);
        Assert.Equal(KvDurability.Sync, client.Transactions[0].Durability);
        Assert.Equal(KvMode.ReadWrite, client.Transactions[0].Mode);
    }

    /// <summary>Reads the fixed-width unsigned big-endian checkpoint format published by Portia 0.1.x.</summary>
    [Fact]
    public async Task ShouldLoadLegacyBigEndianCheckpoint()
    {
        var client = new FakeKvClient();
        var legacy = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(legacy, 4096);
        client.Committed[Encoding.UTF8.GetString(FitzKvCheckpoints.Key(Identity).Span)] = legacy;

        var checkpoint = await new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints").LoadAsync(Identity);

        Assert.Equal(new ProjectionCheckpoint(new EventCursor("4096")), checkpoint);
        Assert.Equal(legacy, Assert.Single(client.Committed).Value);
    }

    /// <summary>Proves a current eight-byte cursor remains opaque because the current format is prefixed.</summary>
    [Fact]
    public async Task ShouldRoundTripEightByteOpaqueCursorWithoutLegacyMisclassification()
    {
        var client = new FakeKvClient();
        var store = new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints");

        await store.SaveAsync(Identity, new ProjectionCheckpoint(new EventCursor("cursor-8")));

        Assert.Equal(new ProjectionCheckpoint(new EventCursor("cursor-8")), await store.LoadAsync(Identity));
    }

    /// <summary>Rejects bytes which are neither a current prefixed cursor nor a legacy fixed-width offset.</summary>
    [Fact]
    public async Task ShouldRejectUnknownUnversionedCheckpointEncoding()
    {
        var client = new FakeKvClient();
        client.Committed[Encoding.UTF8.GetString(FitzKvCheckpoints.Key(Identity).Span)] = "old"u8.ToArray();

        _ = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints").LoadAsync(Identity));
    }

    /// <summary>
    ///     Verifies that component name, pattern, and rebuild generation each key their own checkpoint —
    ///     two reactors sharing a route must never resume from each other's progress, and a rebuild
    ///     generation must never overwrite the live one.
    /// </summary>
    [Fact]
    public async Task ShouldKeepCheckpointsSeparatePerComponentPatternAndRebuildGeneration()
    {
        var client = new FakeKvClient();
        var store = new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints");
        var pattern = EventStreamPattern.ForPattern("tenant", "orders");
        var identities = new[]
        {
            new CheckpointIdentity("reactor", pattern),
            new CheckpointIdentity("other", pattern),
            new CheckpointIdentity("reactor", EventStreamPattern.ForPattern("tenant", "returns")),
            new CheckpointIdentity("reactor", pattern, "rebuild-1")
        };

        for (var index = 0; index < identities.Length; index++)
            await store.SaveAsync(identities[index], new ProjectionCheckpoint(
                new EventCursor(((ulong)index + 1).ToString(CultureInfo.InvariantCulture))));

        Assert.Equal(identities.Length, client.Committed.Count);
        for (var index = 0; index < identities.Length; index++)
            Assert.Equal(((ulong)index + 1).ToString(CultureInfo.InvariantCulture),
                (await store.LoadAsync(identities[index])).Cursor.ToString());
    }

    /// <summary>
    ///     Verifies that two components never contend for the same Fitz KV resource lock even though
    ///     the app configured one shared base route: each component's checkpoint transactions resolve
    ///     to a distinct derived route, so a broker that locks a resource per BEGIN never sees one
    ///     reactor's save block or conflict with another reactor's or projector's.
    /// </summary>
    [Fact]
    public async Task ShouldRouteDifferentComponentsToDifferentFitzKvResources()
    {
        var client = new FakeKvClient();
        var store = new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints");
        var pattern = EventStreamPattern.ForPattern("tenant", "orders");

        await store.SaveAsync(new CheckpointIdentity("reactor-a", pattern),
            new ProjectionCheckpoint(new EventCursor("1")));
        await store.SaveAsync(new CheckpointIdentity("reactor-b", pattern),
            new ProjectionCheckpoint(new EventCursor("1")));

        var routes = client.Transactions.Select(tx => tx.Route).Distinct().ToArray();
        Assert.Equal(2, routes.Length);
        Assert.All(routes,
            route => Assert.StartsWith("kv://portia/state/checkpoints-", route, StringComparison.Ordinal));
    }

    /// <summary>Verifies that the same component always resolves back to the same derived route.</summary>
    [Fact]
    public async Task ShouldRouteTheSameComponentToTheSameFitzKvResourceAcrossCalls()
    {
        var client = new FakeKvClient();
        var store = new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints");

        await store.SaveAsync(Identity, new ProjectionCheckpoint(new EventCursor("1")));
        _ = await store.LoadAsync(Identity);

        Assert.Equal(2, client.Transactions.Count);
        Assert.Equal(client.Transactions[0].Route, client.Transactions[1].Route);
    }

    /// <summary>
    ///     Verifies that every tenant of one per-tenant reactor gets its own Fitz KV resource. Tenant
    ///     instances share a component name and run concurrently, so a route derived from the name
    ///     alone would make every tenant contend for one lock — contention that grows with tenant count.
    /// </summary>
    [Fact]
    public async Task ShouldRouteTenantsOfOneComponentToDifferentFitzKvResources()
    {
        var client = new FakeKvClient();
        var store = new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints");

        await store.SaveAsync(new CheckpointIdentity("reactor", EventStreamPattern.ForPattern("tenant-a", "orders")),
            new ProjectionCheckpoint(new EventCursor("1")));
        await store.SaveAsync(new CheckpointIdentity("reactor", EventStreamPattern.ForPattern("tenant-b", "orders")),
            new ProjectionCheckpoint(new EventCursor("1")));

        Assert.Equal(2, client.Transactions.Select(tx => tx.Route).Distinct().Count());
    }

    /// <summary>
    ///     Verifies that one tenant's in-flight checkpoint write never blocks another tenant's save of
    ///     the same reactor against a broker that locks a whole resource per read-write transaction.
    /// </summary>
    [Fact]
    public async Task ShouldSaveOneTenantWhileAnotherTenantHoldsItsResource()
    {
        var client = new FakeKvClient();
        var store = new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints");
        var tenantA = new CheckpointIdentity("reactor", EventStreamPattern.ForPattern("tenant-a", "orders"));
        var tenantB = new CheckpointIdentity("reactor", EventStreamPattern.ForPattern("tenant-b", "orders"));
        await store.SaveAsync(tenantA, new ProjectionCheckpoint(new EventCursor("1")));
        await using var held = await client.BeginAsync(client.LastTransaction.Route, KvDurability.Sync);

        await store.SaveAsync(tenantB, new ProjectionCheckpoint(new EventCursor("1")));

        Assert.Equal("1", (await store.LoadAsync(tenantB)).Cursor.ToString());
    }

    /// <summary>
    ///     Verifies that a Fitz KV isolation conflict becomes <see cref="ProjectionConcurrencyException" />
    ///     so one catch covers every checkpoint store, with the broker's own exception kept as the inner
    ///     cause and the losing write rolled back rather than left staged.
    /// </summary>
    [Fact]
    public async Task ShouldTranslateIsolationConflictToProjectionConcurrencyException()
    {
        var conflict = new KvException("conflict", "TX_CONFLICT", domainCode: FitzErrorCodes.KvIsolationConflict);
        var client = new FakeKvClient { CommitFailure = conflict };
        var store = new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints");

        var error = await Assert.ThrowsAsync<ProjectionConcurrencyException>(async () =>
            await store.SaveAsync(Identity, new ProjectionCheckpoint(new EventCursor("1"))));

        Assert.Same(conflict, error.InnerException);
        Assert.Contains("reactor", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, client.LastTransaction.Rollbacks);
        Assert.Equal(1, client.LastTransaction.Disposals);
        Assert.Empty(client.Committed);
    }

    /// <summary>
    ///     Verifies a conflict names the component and stream area but not the bound pattern, whose
    ///     realm is a tenant for a per-tenant workload and whose resource can be an aggregate. The
    ///     message reaches error logs and trace exception events, which must not carry tenant data.
    /// </summary>
    [Fact]
    public async Task ShouldKeepTenantAndAggregateIdentifiersOutOfTheConflictMessage()
    {
        var conflict = new KvException("conflict", "TX_CONFLICT", domainCode: FitzErrorCodes.KvIsolationConflict);
        var client = new FakeKvClient { CommitFailure = conflict };
        var store = new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints");
        var identity = new CheckpointIdentity("reactor",
            EventStreamPattern.ForPattern("tenant-7f3a", "orders", "order-5c21"));

        var error = await Assert.ThrowsAsync<ProjectionConcurrencyException>(async () =>
            await store.SaveAsync(identity, new ProjectionCheckpoint(new EventCursor("1"))));

        Assert.Contains("reactor", error.Message, StringComparison.Ordinal);
        Assert.Contains("orders", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-7f3a", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("order-5c21", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that every other failure reaches the caller unchanged — only a structured isolation
    ///     conflict is retryable, so a transport or backend fault must not be disguised as one.
    /// </summary>
    /// <param name="atCommit"><see langword="true" /> to fail the commit; otherwise the staged put.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRethrowNonConflictFailureUnchanged(bool atCommit)
    {
        var failure = new KvException("backend down", "KV_BACKEND", domainCode: FitzErrorCodes.KvBackendError);
        var client = new FakeKvClient();
        if (atCommit)
        {
            client.CommitFailure = failure;
        }
        else
        {
            client.PutFailure = failure;
        }

        var store = new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints");

        var error = await Assert.ThrowsAsync<KvException>(async () =>
            await store.SaveAsync(Identity, new ProjectionCheckpoint(new EventCursor("1"))));

        Assert.Same(failure, error);
        Assert.Equal(1, client.LastTransaction.Rollbacks);
        Assert.Equal(1, client.LastTransaction.Disposals);
        Assert.Empty(client.Committed);
    }

    /// <summary>
    ///     Verifies that cleanup never replaces the failure the caller needs to see: a rollback and a
    ///     disposal that both fail after a failed commit are swallowed, leaving the original commit
    ///     failure — the one that says whether the checkpoint advanced — as the thrown exception.
    /// </summary>
    [Fact]
    public async Task ShouldPreserveCommitFailureWhenRollbackAndDisposalAlsoFail()
    {
        var failure = new KvException("commit failed", "KV_BACKEND", domainCode: FitzErrorCodes.KvBackendError);
        var client = new FakeKvClient
        {
            CommitFailure = failure,
            RollbackFailure = new IOException("rollback failed"),
            DisposeFailure = new IOException("dispose failed")
        };

        var error = await Assert.ThrowsAsync<KvException>(async () =>
            await new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints")
                .SaveAsync(Identity, new ProjectionCheckpoint(new EventCursor("1"))));

        Assert.Same(failure, error);
        Assert.Equal(1, client.LastTransaction.Rollbacks);
        Assert.Equal(1, client.LastTransaction.Disposals);
    }

    /// <summary>
    ///     Verifies the other half of that rule: when the save itself succeeded there is no failure to
    ///     preserve, so a disposal fault is surfaced rather than silently swallowed — a broker that
    ///     cannot close its own transaction is not something to hide from the caller.
    /// </summary>
    [Fact]
    public async Task ShouldSurfaceDisposalFailureWhenTheSaveSucceeded()
    {
        var client = new FakeKvClient { DisposeFailure = new IOException("dispose failed") };

        _ = await Assert.ThrowsAsync<IOException>(async () =>
            await new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints")
                .SaveAsync(Identity, new ProjectionCheckpoint(new EventCursor("1"))));

        Assert.Equal(0, client.LastTransaction.Rollbacks);
        Assert.Single(client.Committed);
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
            _ = Assert.Throws<ArgumentNullException>(() => new FitzKvCheckpointStore(null!, "kv://portia/state/x"));
        }
        else
        {
            _ = Assert.Throws<ArgumentException>(() => new FitzKvCheckpointStore(new FakeKvClient(), route));
        }
    }

    /// <summary>
    ///     Verifies that a missing identity is rejected before a transaction is opened, so a caller's bug
    ///     never leaves an abandoned transaction against the broker.
    /// </summary>
    /// <param name="saving"><see langword="true" /> to check the save path; otherwise the load path.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectMissingIdentityBeforeOpeningATransaction(bool saving)
    {
        var client = new FakeKvClient();
        var store = new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints");

        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            if (saving)
            {
                await store.SaveAsync(null!, ProjectionCheckpoint.Start);
            }
            else
            {
                _ = await store.LoadAsync(null!);
            }
        });

        Assert.Empty(client.Transactions);
    }
}
