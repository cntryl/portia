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
    public async Task ShouldKeepSignallingGivenCallerCancelledOneWait()
    {
        // Arrange
        var commits = Channel.CreateUnbounded<StreamCommitEvent>();
        var client = new SubscribingStreamClient(commits.Reader);
        var store = new FitzEventStore(client, TestJson.DomainSerializer(new DomainEventTypeCatalog()));

        // Act
        await using var subscription = await store.SubscribeAsync(Pattern);

        // Assert
        using (var backstop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await subscription.WaitAsync(backstop.Token));
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
    public async Task ShouldReleaseUnderlyingSubscriptionGivenActiveSubscriptionWhenDisposed()
    {
        // Arrange
        var commits = Channel.CreateUnbounded<StreamCommitEvent>();
        var client = new SubscribingStreamClient(commits.Reader);
        var store = new FitzEventStore(client, TestJson.DomainSerializer(new DomainEventTypeCatalog()));

        // Act
        var subscription = await store.SubscribeAsync(Pattern);

        // Assert
        using (var backstop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await subscription.WaitAsync(backstop.Token));
        }

        await subscription.DisposeAsync();

        Assert.Equal(1, client.Unsubscribes);
    }

    /// <summary>
    ///     Verifies disposal after a retained wait faulted still releases the underlying subscription
    ///     and does not rethrow the fault the caller already observed from that wait.
    /// </summary>
    [Fact]
    public async Task ShouldReleaseUnderlyingSubscriptionGivenFaultedWaitWhenDisposed()
    {
        // Arrange
        var commits = Channel.CreateUnbounded<StreamCommitEvent>();
        var client = new SubscribingStreamClient(commits.Reader);
        var store = new FitzEventStore(client, TestJson.DomainSerializer(new DomainEventTypeCatalog()));

        // Act
        var subscription = await store.SubscribeAsync(Pattern);

        // Assert
        using (var backstop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await subscription.WaitAsync(backstop.Token));
        }

        _ = commits.Writer.TryComplete(new IOException("broker connection lost"));
        _ = await Assert.ThrowsAsync<IOException>(async () => await subscription.WaitAsync());

        await subscription.DisposeAsync();

        Assert.Equal(1, client.Unsubscribes);
    }

    /// <summary>
    ///     Verifies a wait consumes every commit notification already buffered. A workload that
    ///     keeps finding work does not wait between passes, so without draining, its commits
    ///     accumulate until Fitz ends the subscription at the bound of its buffer.
    /// </summary>
    [Fact]
    public async Task ShouldDrainBufferedCommitsGivenSubscriptionWhenWaitingEachRound()
    {
        // Arrange
        var client = new BoundedStreamClient(4);
        var store = new FitzEventStore(client, TestJson.DomainSerializer(new DomainEventTypeCatalog()));
        await using var subscription = await store.SubscribeAsync(Pattern);

        // Act
        for (var round = 0; round < 5; round++)
        {
            client.Commit(3);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await subscription.WaitAsync(wait.Token);
        }

        // Assert
        Assert.Equal(0, client.Overflows);
        using var idle = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await subscription.WaitAsync(idle.Token));
    }

    /// <summary>
    ///     Verifies an overflowed subscription is a wake-up, not a failure: the caller re-reads
    ///     durable state after any wake-up, so the lost notifications cost nothing once the
    ///     subscription is replaced — whereas a fault is logged and costs a poll interval.
    /// </summary>
    [Fact]
    public async Task ShouldResubscribeGivenOverflowedSubscriptionWhenWaiting()
    {
        // Arrange
        var client = new BoundedStreamClient(2);
        var store = new FitzEventStore(client, TestJson.DomainSerializer(new DomainEventTypeCatalog()));
        await using var subscription = await store.SubscribeAsync(Pattern);
        client.Commit(3);

        // Act
        using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            await subscription.WaitAsync(wait.Token);

        // Assert
        Assert.Equal(2, client.Subscriptions);
        Assert.Equal(1, client.Unsubscribes);
        using (var idle = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await subscription.WaitAsync(idle.Token));
        }

        client.Commit(1);
        using var next = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await subscription.WaitAsync(next.Token);
    }

    /// <summary>
    ///     Verifies disposal that finishes while a replacement subscription is still being established
    ///     neither faults the waiter nor leaves the replacement subscribed.
    /// </summary>
    [Fact]
    public async Task ShouldReleaseReplacementGivenDisposedWhileResubscribing()
    {
        // Arrange
        var client = new GatedResubscribeClient();
        var store = new FitzEventStore(client, TestJson.DomainSerializer(new DomainEventTypeCatalog()));
        var subscription = await store.SubscribeAsync(Pattern);
        client.Overflow();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var wait = subscription.WaitAsync(deadline.Token).AsTask();
        await client.ReplacementRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        await subscription.DisposeAsync();
        client.ReleaseReplacement();
        var failure = await Record.ExceptionAsync(() => wait.WaitAsync(TimeSpan.FromSeconds(5)));

        // Assert
        Assert.Null(failure);
        Assert.Equal(1, client.ReplacementUnsubscribes);
    }

    // The second subscription is held until released, as a slow broker would hold it.
    sealed class GatedResubscribeClient : IStreamClient
    {
        readonly Channel<StreamCommitEvent> _first = Channel.CreateBounded<StreamCommitEvent>(1);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _replacementUnsubscribes;
        int _subscriptions;

        public TaskCompletionSource ReplacementRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReplacementUnsubscribes => Volatile.Read(ref _replacementUnsubscribes);

        public void Overflow() => _ = _first.Writer.TryComplete(
            new SubscriptionBackpressureException("The local subscription buffer is full"));

        public void ReleaseReplacement() => _release.TrySetResult();

        public async Task<StreamSubscription> SubscribeAsync(string pattern, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _subscriptions) == 1)
            {
                return new StreamSubscription(pattern, _first.Reader.ReadAllAsync(CancellationToken.None),
                    static _ => ValueTask.CompletedTask);
            }

            _ = ReplacementRequested.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            var replacement = Channel.CreateUnbounded<StreamCommitEvent>();
            return new StreamSubscription(pattern, replacement.Reader.ReadAllAsync(CancellationToken.None),
                token =>
                {
                    _ = Interlocked.Increment(ref _replacementUnsubscribes);
                    _ = replacement.Writer.TryComplete();
                    return ValueTask.CompletedTask;
                });
        }

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
    }

    // Buffers commit notifications per subscription and ends one with
    // SubscriptionBackpressureException on overflow, as Fitz's own stream subscription does.
    sealed class BoundedStreamClient(int capacity) : IStreamClient
    {
        readonly Lock _gate = new();
        Channel<StreamCommitEvent>? _commits;
        ulong _offset;
        int _overflows;
        int _subscriptions;
        int _unsubscribes;

        public int Overflows => Volatile.Read(ref _overflows);
        public int Subscriptions => Volatile.Read(ref _subscriptions);
        public int Unsubscribes => Volatile.Read(ref _unsubscribes);

        public void Commit(int count)
        {
            lock (_gate)
            {
                for (var index = 0; index < count; index++)
                {
                    if (_commits!.Writer.TryWrite(new StreamCommitEvent(Pattern.ToString(), _offset++)))
                        continue;
                    _ = Interlocked.Increment(ref _overflows);
                    _ = _commits.Writer.TryComplete(
                        new SubscriptionBackpressureException("The local subscription buffer is full"));
                }
            }
        }

        public Task<StreamSubscription> SubscribeAsync(string pattern, CancellationToken ct = default)
        {
            var commits = Channel.CreateBounded<StreamCommitEvent>(capacity);
            lock (_gate)
                _commits = commits;
            _ = Interlocked.Increment(ref _subscriptions);
            return Task.FromResult(new StreamSubscription(pattern,
                commits.Reader.ReadAllAsync(CancellationToken.None), Unsubscribe));
        }

        ValueTask Unsubscribe(CancellationToken ct)
        {
            _ = Interlocked.Increment(ref _unsubscribes);
            return ValueTask.CompletedTask;
        }

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
