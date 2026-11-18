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
    public async Task ShouldRejectRepositoryWritesGivenDisposedBatch()
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
    public async Task ShouldRejectRepositoryWritesGivenNoOpenBatch()
    {
        // Arrange
        var store = new StrayWriteRepository(new FakeKvClient(), "kv://portia/state/orders");

        // Act
        var exception = await Record.ExceptionAsync(() => store.AddAsync("total", 12));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>
    ///     Verifies a batch that has committed refuses a second commit instead of handing a finished
    ///     transaction another checkpoint write.
    /// </summary>
    [Fact]
    public async Task ShouldRefuseCommitGivenBatchAlreadyCommitted()
    {
        // Arrange
        var store = new StrayWriteRepository(new FakeKvClient(), "kv://portia/state/orders");
        await using var batch =
            await store.BeginAsync(new ProjectionBatchContext(Identity, ProjectionCheckpoint.Start));
        await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("1")));

        // Act
        var exception = await Record.ExceptionAsync(() =>
            batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("2"))).AsTask());

        // Assert
        var error = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("already finished", error.Message, StringComparison.Ordinal);
        Assert.Equal("1", (await store.LoadCheckpointAsync(Identity)).Cursor.ToString());
    }

    /// <summary>Verifies a disposed batch refuses to commit.</summary>
    [Fact]
    public async Task ShouldRefuseCommitGivenBatchAlreadyDisposed()
    {
        // Arrange
        var store = new StrayWriteRepository(new FakeKvClient(), "kv://portia/state/orders");
        var batch = await store.BeginAsync(new ProjectionBatchContext(Identity, ProjectionCheckpoint.Start));
        await batch.DisposeAsync();

        // Act
        var exception = await Record.ExceptionAsync(() =>
            batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("1"))).AsTask());

        // Assert
        var error = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("already finished", error.Message, StringComparison.Ordinal);
        Assert.Equal(ProjectionCheckpoint.Start, await store.LoadCheckpointAsync(Identity));
    }

    sealed class StrayWriteRepository(IKvClient client, string route)
        : FitzKvProjectionStore(client, route, "orders")
    {
        public Task AddAsync(string name, int amount) =>
            Transaction.PutAsync(Encoding.UTF8.GetBytes("total\0" + name),
                Encoding.UTF8.GetBytes(amount.ToString(CultureInfo.InvariantCulture)));
    }
}
