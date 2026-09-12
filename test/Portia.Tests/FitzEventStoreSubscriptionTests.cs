using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Cntryl.Portia;

/// <summary>
///     Covers <see cref="IDomainEventSubscription" />'s cancellation contract as the Fitz adapter
///     implements it: the token passed to <see cref="IDomainEventSubscription.WaitAsync" /> cancels
///     that wait, while the subscription itself signals until it is disposed.
/// </summary>
public sealed class FitzEventStoreSubscriptionTests
{
    static readonly EventStreamPattern Pattern = EventStreamPattern.ForPattern("tenant", "orders", "one");

    /// <summary>
    ///     Verifies a caller that bounds one wait still receives the next commit. Portia's own worker
    ///     loop wraps every wait in a poll-interval backstop, so a timed-out wait that tore the
    ///     subscription down would leave the workload notified exactly once and polling thereafter.
    /// </summary>
    [Fact]
    public async Task ShouldKeepSignallingAfterACallerCancelsOneWait()
    {
        var commits = Channel.CreateUnbounded<StreamCommitEvent>();
        var client = new SubscribingStreamClient(commits.Reader);
        var store = new FitzEventStore(client, TestJson.DomainSerializer(new DomainEventTypeCatalog()));
        await using var subscription = await store.SubscribeAsync(Pattern);

        using (var backstop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await subscription.WaitAsync(backstop.Token));
        }

        Assert.Equal(0, client.Unsubscribes);
        _ = commits.Writer.TryWrite(new StreamCommitEvent(Pattern.ToString(), 0));

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await subscription.WaitAsync(wait.Token);
    }

    /// <summary>
    ///     Verifies disposal still ends the subscription, so a cancelled wait no longer being terminal
    ///     does not leave the underlying Fitz subscription running for the life of the process.
    /// </summary>
    [Fact]
    public async Task ShouldReleaseTheUnderlyingSubscriptionOnDisposal()
    {
        var commits = Channel.CreateUnbounded<StreamCommitEvent>();
        var client = new SubscribingStreamClient(commits.Reader);
        var store = new FitzEventStore(client, TestJson.DomainSerializer(new DomainEventTypeCatalog()));
        var subscription = await store.SubscribeAsync(Pattern);

        using (var backstop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await subscription.WaitAsync(backstop.Token));
        }

        await subscription.DisposeAsync();

        Assert.Equal(1, client.Unsubscribes);
    }

    sealed class SubscribingStreamClient(ChannelReader<StreamCommitEvent> commits) : IStreamClient
    {
        int _unsubscribes;

        public int Unsubscribes => Volatile.Read(ref _unsubscribes);

        public Task<StreamSubscription> SubscribeAsync(string pattern, CancellationToken ct = default) =>
            Task.FromResult(new StreamSubscription(pattern, Read(ct), Unsubscribe, Task.CompletedTask));

        public Task<StreamReadPage> ReadPageAsync(string route, ulong startOffset, ulong limit = 100,
            StreamFilterSet? filter = null, ulong? maxBytes = null, ulong? cursorFingerprint = null,
            ulong? capturedWatermark = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IStreamSession> BeginAsync(string route, ReadOnlyMemory<byte>? ingestMetadata = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public IAsyncEnumerable<StreamRecord> ReadAsync(string route, ulong startOffset, ulong limit = 100,
            StreamFilterSet? filter = null, ulong? maxBytes = null, ulong? cursorFingerprint = null,
            ulong? capturedWatermark = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StreamRecord?> PeekAsync(string route, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StreamMetadata> MetadataAsync(string route, CancellationToken ct = default) =>
            throw new NotSupportedException();

        ValueTask Unsubscribe(CancellationToken ct)
        {
            _ = Interlocked.Increment(ref _unsubscribes);
            return ValueTask.CompletedTask;
        }

        async IAsyncEnumerable<StreamCommitEvent> Read([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var commit in commits.ReadAllAsync(ct).ConfigureAwait(false))
                yield return commit;
        }
    }
}
