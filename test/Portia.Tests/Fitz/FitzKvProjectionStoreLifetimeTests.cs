using System.Globalization;
using System.Text;

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
        // Arrange
        var client = new FakeKvClient();
        var store = new StrayWriteRepository(client, "kv://portia/state/orders");

        // Act
        await using (var batch =
                     await store.BeginAsync(new ProjectionBatchContext(Identity, ProjectionCheckpoint.Start)))
            await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("1")));

        // Assert
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
        // Arrange
        var store = new StrayWriteRepository(new FakeKvClient(), "kv://portia/state/orders");

        // Act
        var exception = await Record.ExceptionAsync(() => store.AddAsync("total", 12));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(exception);
    }

    sealed class StrayWriteRepository(IKvClient client, string route)
        : FitzKvProjectionStore(client, route, "orders")
    {
        public Task AddAsync(string name, int amount) =>
            Transaction.PutAsync(Encoding.UTF8.GetBytes("total\0" + name),
                Encoding.UTF8.GetBytes(amount.ToString(CultureInfo.InvariantCulture)));
    }
}
