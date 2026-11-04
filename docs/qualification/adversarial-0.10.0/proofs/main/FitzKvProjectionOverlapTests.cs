using System.Text;

namespace Cntryl.Portia;

/// <summary>Checks the single-batch boundary before asynchronous transaction acquisition completes.</summary>
public sealed class FitzKvProjectionOverlapTests
{
    /// <summary>A delayed second acquisition must not retarget writes outside the first batch's checkpoint transaction.</summary>
    [Fact]
    public async Task ShouldKeepDomainWritesWithTheirCheckpointWhenBeginCallsOverlap()
    {
        var client = new GatedKvClient();
        var repository = new OverlapRepository(client);
        var first = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("first", "orders"));
        var second = new CheckpointIdentity("orders", EventStreamPattern.ForPattern("second", "orders"));
        var firstOpen = repository.BeginAsync(new ProjectionBatchContext(first, ProjectionCheckpoint.Start)).AsTask();
        await client.FirstArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondOpen = repository.BeginAsync(new ProjectionBatchContext(second, ProjectionCheckpoint.Start)).AsTask();
        _ = await Task.WhenAny(client.BothArrived.Task, secondOpen).WaitAsync(TimeSpan.FromSeconds(5));
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

    sealed class OverlapRepository(IKvClient client) : FitzKvProjectionStore(client, "kv://review/overlap/orders", "orders")
    {
        internal static ReadOnlyMemory<byte> DataKey => "value"u8.ToArray();
        internal string? WriteRoute { get; private set; }

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
