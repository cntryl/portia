using System.Text;
using Cntryl.Fitz.Abstractions.Domains.Kv;

namespace Cntryl.Portia;

/// <summary>
///     Covers the window in which a <see cref="FitzKvProjectionStore" />'s shared transaction is
///     valid — between <c>BeginAsync</c> and its batch's disposal, and never outside it.
/// </summary>
public sealed class FitzKvProjectionStoreLifetimeTests
{
    static readonly CheckpointIdentity Identity = new("orders", EventStreamPattern.ForPattern("tenant", "orders"));

    /// <summary>
    ///     Verifies a repository write attempted outside a batch fails immediately and says why,
    ///     instead of staging into the previous batch's already-disposed transaction where the write
    ///     is silently lost.
    /// </summary>
    [Fact]
    public async Task ShouldRejectRepositoryWritesAfterTheBatchIsDisposed()
    {
        var client = new FakeKvClient();
        var store = new StrayWriteRepository(client, "kv://portia/state/orders");

        await using (var batch = await store.BeginAsync(new ProjectionBatchContext(Identity, ProjectionCheckpoint.Start)))
            await batch.CommitAsync(new ProjectionCheckpoint(1));

        var stray = await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync("total", 12));

        Assert.Contains("batch", stray.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Verifies a repository write before any batch is opened fails the same way, rather than
    ///     dereferencing the uninitialized transaction.
    /// </summary>
    [Fact]
    public async Task ShouldRejectRepositoryWritesBeforeAnyBatchIsOpened()
    {
        var store = new StrayWriteRepository(new FakeKvClient(), "kv://portia/state/orders");

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync("total", 12));
    }

    sealed class StrayWriteRepository(IKvClient client, string route) : FitzKvProjectionStore(client, route)
    {
        public Task AddAsync(string name, int amount) =>
            Transaction.PutAsync(Encoding.UTF8.GetBytes("total\0" + name),
                Encoding.UTF8.GetBytes(amount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }
}
