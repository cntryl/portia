using System.Buffers.Binary;
using Cntryl.Fitz.Abstractions;
using Cntryl.Fitz.Abstractions.Domains.Kv;
using Cntryl.Fitz.Errors;

namespace Cntryl.Portia;

/// <summary>
///     Verifies the durable reactor checkpoint store backed by Fitz KV: what it writes, what it reads
///     back, and — the part a reactor's exactly-once story depends on — what it does when the write
///     fails partway through.
/// </summary>
public sealed class FitzKvCheckpointStoreTests
{
    static readonly CheckpointIdentity Identity = new("reactor", EventStreamPattern.ForPattern("tenant", "orders"));

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
    ///     Verifies that a saved checkpoint reads back as the same offset, stored as a fixed-width
    ///     big-endian value so the bytes sort in the same order as the offsets they encode.
    /// </summary>
    [Fact]
    public async Task ShouldRoundTripSavedCheckpointAsBigEndianBytes()
    {
        var client = new FakeKvClient();
        var store = new FitzKvCheckpointStore(client, "kv://portia/state/checkpoints");

        await store.SaveAsync(Identity, new ProjectionCheckpoint(4096));

        Assert.Equal(new ProjectionCheckpoint(4096), await store.LoadAsync(Identity));
        var stored = Assert.Single(client.Committed).Value;
        Assert.Equal(sizeof(ulong), stored.Length);
        Assert.Equal(4096UL, BinaryPrimitives.ReadUInt64BigEndian(stored));
        Assert.Equal(KvDurability.Sync, client.Transactions[0].Durability);
        Assert.Equal(KvMode.ReadWrite, client.Transactions[0].Mode);
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
            await store.SaveAsync(identities[index], new ProjectionCheckpoint((ulong)index + 1));

        Assert.Equal(identities.Length, client.Committed.Count);
        for (var index = 0; index < identities.Length; index++)
            Assert.Equal((ulong)index + 1, (await store.LoadAsync(identities[index])).NextOffset);
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
            await store.SaveAsync(Identity, new ProjectionCheckpoint(1)));

        Assert.Same(conflict, error.InnerException);
        Assert.Contains("reactor", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, client.LastTransaction.Rollbacks);
        Assert.Equal(1, client.LastTransaction.Disposals);
        Assert.Empty(client.Committed);
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
            await store.SaveAsync(Identity, new ProjectionCheckpoint(1)));

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
                .SaveAsync(Identity, new ProjectionCheckpoint(1)));

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
                .SaveAsync(Identity, new ProjectionCheckpoint(1)));

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
