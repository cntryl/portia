using System.Text;

namespace Cntryl.Portia;

/// <summary>Checks the single-batch boundary before asynchronous transaction acquisition completes.</summary>
public sealed class FitzKvProjectionOverlapTests
{
    /// <summary>A delayed second acquisition must not retarget writes outside the first batch's checkpoint transaction.</summary>
    [Fact]
    public async Task ShouldKeepDomainWritesWithTheirCheckpointWhenBeginCallsOverlap()
    {
        // Arrange
        var client = new GatedKvClient();
        var repository = new OverlapRepository(client);
        var first = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("first", "orders"));
        var second = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("second", "orders"));
        var firstOpen = repository.BeginAsync(new ProjectionBatchContext(first, ProjectionCheckpoint.Start)).AsTask();
        await client.FirstArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondOpen = repository.BeginAsync(new ProjectionBatchContext(second, ProjectionCheckpoint.Start)).AsTask();

        // Act
        _ = await Task.WhenAny(client.BothArrived.Task, secondOpen).WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        if (secondOpen.IsFaulted)
        {
            _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await secondOpen);
            client.ReleaseFirst.TrySetResult();
            await using var accepted = await firstOpen.WaitAsync(TimeSpan.FromSeconds(5));
            await repository.StageAsync("first-batch-value");
            var acceptedNext = new ProjectionCheckpoint(new EventCursor("1"));
            await accepted.CommitAsync(acceptedNext);
            Assert.Equal(acceptedNext.Cursor, FitzKvCheckpoints.Decode(client.Inner.Read(FitzKvCheckpoints.Key(first))!));
            Assert.Equal("first-batch-value", client.Inner.Read(OverlapRepository.DataKey) is { } acceptedBytes
                ? Encoding.UTF8.GetString(acceptedBytes) : null);
            return;
        }
        client.ReleaseFirst.TrySetResult();
        await using var firstBatch = await firstOpen.WaitAsync(TimeSpan.FromSeconds(5));
        client.ReleaseSecond.TrySetResult();
        await using var secondBatch = await secondOpen.WaitAsync(TimeSpan.FromSeconds(5));

        await repository.StageAsync("first-batch-value");
        Assert.Equal(client.Inner.Transactions[1].Route, repository.WriteRoute);
        var next = new ProjectionCheckpoint(new EventCursor("1"));
        await firstBatch.CommitAsync(next);

        Assert.Equal(next.Cursor, FitzKvCheckpoints.Decode(client.Inner.Read(FitzKvCheckpoints.Key(first))!));
        Assert.Equal("first-batch-value", client.Inner.Read(OverlapRepository.DataKey) is { } bytes
            ? Encoding.UTF8.GetString(bytes) : null);
    }

    /// <summary>A query-side read cannot start while the store is still acquiring its batch transaction.</summary>
    [Fact]
    public async Task ShouldRejectQueryReadWhileNativeBeginIsPending()
    {
        // Arrange
        var client = new GatedKvClient();
        var repository = new OverlapRepository(client);
        var identity = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("tenant", "orders"));
        var opening = repository.BeginAsync(new ProjectionBatchContext(identity, ProjectionCheckpoint.Start)).AsTask();

        // Act
        await client.FirstArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        try
        {
            _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await repository.ReadAsync("tenant").AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Single(client.Inner.Transactions);
        }
        finally
        {
            client.ReleaseFirst.TrySetResult();
            client.ReleaseSecond.TrySetResult();
        }
        await using var batch = await opening.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>A failed native acquisition releases the reserved slot for the next batch.</summary>
    [Fact]
    public async Task ShouldReleaseBatchSlotWhenNativeBeginFails()
    {
        // Arrange
        var client = new FailFirstBeginClient();
        var repository = new OverlapRepository(client);
        var identity = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("tenant", "orders"));

        // Act
        var context = new ProjectionBatchContext(identity, ProjectionCheckpoint.Start);

        // Assert
        _ = await Assert.ThrowsAsync<IOException>(async () => await repository.BeginAsync(context));

        await using var batch = await repository.BeginAsync(context);
        await repository.StageAsync("reused");
        await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("1")));

        Assert.Equal("reused", Encoding.UTF8.GetString(client.Inner.Read(OverlapRepository.DataKey)!));
    }

    /// <summary>Disposal failure must not leave the store's local slot permanently reserved.</summary>
    [Fact]
    public async Task ShouldReleaseBatchSlotWhenNativeDisposeFails()
    {
        // Arrange
        var client = new FakeKvClient { DisposeFailure = new IOException("dispose failed") };
        var repository = new OverlapRepository(client);
        var identity = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("tenant", "orders"));
        var context = new ProjectionBatchContext(identity, ProjectionCheckpoint.Start);

        // Act
        var failed = await repository.BeginAsync(context);

        // Assert
        _ = await Assert.ThrowsAsync<IOException>(async () => await failed.DisposeAsync());
        client.DisposeFailure = null;

        await using var batch = await repository.BeginAsync(context);
        await repository.StageAsync("reused");
        await batch.CommitAsync(new ProjectionCheckpoint(new EventCursor("1")));

        Assert.Equal("reused", Encoding.UTF8.GetString(client.Read(OverlapRepository.DataKey)!));
    }

    /// <summary>Disposing an old batch twice cannot release the next batch's reserved slot.</summary>
    [Fact]
    public async Task ShouldKeepCurrentSlotReservedWhenPreviousBatchIsDisposedAgain()
    {
        // Arrange
        var repository = new OverlapRepository(new FakeKvClient());
        var identity = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("tenant", "orders"));
        var context = new ProjectionBatchContext(identity, ProjectionCheckpoint.Start);
        var previous = await repository.BeginAsync(context);
        await previous.DisposeAsync();
        await using var current = await repository.BeginAsync(context);

        // Act
        await previous.DisposeAsync();

        // Assert
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await repository.BeginAsync(context));
        await repository.StageAsync("current");
        await current.CommitAsync(new ProjectionCheckpoint(new EventCursor("1")));
    }

    sealed class FailFirstBeginClient : IKvClient
    {
        bool _failed;
        internal FakeKvClient Inner { get; } = new();

        public Task<IKvTransaction> BeginAsync(string route, KvMode mode = KvMode.ReadWrite,
            CancellationToken cancellationToken = default)
        {
            if (_failed)
                return Inner.BeginAsync(route, mode, cancellationToken);
            _failed = true;
            return Task.FromException<IKvTransaction>(new IOException("begin failed"));
        }

        public Task<KvSubscription> SubscribeAsync(string pattern, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    sealed class OverlapRepository(IKvClient client) : FitzKvProjectionStore(client, "kv://review/overlap/orders", "orders")
    {
        internal static ReadOnlyMemory<byte> DataKey => "value"u8.ToArray();
        internal string? WriteRoute { get; private set; }

        internal async ValueTask ReadAsync(string realm)
        {
            await using var transaction = await BeginReadAsync(realm);
        }

        internal Task StageAsync(string value)
        {
            WriteRoute = Transaction.Route;
            return Transaction.PutAsync(DataKey, Encoding.UTF8.GetBytes(value));
        }
    }

    sealed class GatedKvClient : IKvClient
    {
        int _begins;
        internal FakeKvClient Inner { get; } = new();
        internal TaskCompletionSource FirstArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource BothArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IKvTransaction> BeginAsync(string route, KvMode mode = KvMode.ReadWrite,
            CancellationToken cancellationToken = default)
        {
            var transaction = await Inner.BeginAsync(route, mode, cancellationToken);
            if (Interlocked.Increment(ref _begins) == 1)
            {
                FirstArrived.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                BothArrived.TrySetResult();
                await ReleaseSecond.Task.WaitAsync(cancellationToken);
            }
            return transaction;
        }

        public Task<KvSubscription> SubscribeAsync(string pattern, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
