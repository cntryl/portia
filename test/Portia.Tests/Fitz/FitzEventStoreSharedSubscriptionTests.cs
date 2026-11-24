using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Cntryl.Portia;

/// <summary>Regression coverage for sharing one Fitz wake-up source across logical subscribers.</summary>
public sealed class FitzEventStoreSharedSubscriptionTests
{
    static readonly EventStreamPattern Pattern = EventStreamPattern.ForPattern("tenant", "orders", "one");

    /// <summary>Only independently addressable pattern strings receive independent broker subscriptions.</summary>
    [Fact]
    public async Task ShouldShareUnderlyingSubscriptionGivenExactMatchingPatterns()
    {
        // Arrange
        var client = new RecordingStreamClient();
        var store = new FitzEventStore(client, TestJson.DomainSerializer(new DomainEventTypeCatalog()));

        // Act
        await using var first = await store.SubscribeAsync(Pattern, TestContext.Current.CancellationToken);
        await using var matching = await store.SubscribeAsync(
            EventStreamPattern.ForPattern("tenant", "orders", "one"), TestContext.Current.CancellationToken);
        await using var other = await store.SubscribeAsync(
            EventStreamPattern.ForPattern("tenant", "orders", "two"), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, client.Subscriptions);
        Assert.Equal(
            [Pattern.ToString(), EventStreamPattern.ForPattern("tenant", "orders", "two").ToString()],
            client.Patterns);
    }

    /// <summary>The shared source does not turn subscriber count into a fixed-capacity limit.</summary>
    [Fact]
    public async Task ShouldShareUnderlyingSubscriptionGivenMoreThan128LogicalSubscribers()
    {
        // Arrange
        var client = new RecordingStreamClient();
        var store = new FitzEventStore(client, TestJson.DomainSerializer(new DomainEventTypeCatalog()));
        var subscriptions = new List<IDomainEventSubscription>();

        // Act
        for (var index = 0; index < 129; index++)
            subscriptions.Add(await store.SubscribeAsync(Pattern, TestContext.Current.CancellationToken));

        // Assert
        try
        {
            Assert.Equal(1, client.Subscriptions);
        }
        finally
        {
            await Task.WhenAll(subscriptions.Select(subscription => subscription.DisposeAsync().AsTask()));
        }
    }

    /// <summary>One source commit broadcasts a wake-up to every logical subscriber waiting on it.</summary>
    [Fact]
    public async Task ShouldWakeAllLogicalSubscribersGivenOneCommit()
    {
        // Arrange
        var client = new RecordingStreamClient();
        var store = new FitzEventStore(client, TestJson.DomainSerializer(new DomainEventTypeCatalog()));
        await using var first = await store.SubscribeAsync(Pattern, TestContext.Current.CancellationToken);
        await using var second = await store.SubscribeAsync(Pattern, TestContext.Current.CancellationToken);
        var firstWait = first.WaitAsync(TestContext.Current.CancellationToken).AsTask();
        var secondWait = second.WaitAsync(TestContext.Current.CancellationToken).AsTask();

        // Act
        client.Commit(Pattern.ToString());
        await Task.WhenAll(firstWait, secondWait)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, client.Subscriptions);
    }

    /// <summary>A slower logical subscriber retains a wake-up after a peer has consumed its own.</summary>
    [Fact]
    public async Task ShouldRetainWakeupGivenSubscriberDidNotWaitYet()
    {
        // Arrange
        var client = new RecordingStreamClient();
        var store = new FitzEventStore(client, TestJson.DomainSerializer(new DomainEventTypeCatalog()));
        await using var first = await store.SubscribeAsync(Pattern, TestContext.Current.CancellationToken);
        await using var second = await store.SubscribeAsync(Pattern, TestContext.Current.CancellationToken);

        // Act
        client.Commit(Pattern.ToString());
        await first.WaitAsync(TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await second.WaitAsync(TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, client.Subscriptions);
    }

    /// <summary>Cancelling one logical wait neither cancels a peer nor removes that subscriber's signal.</summary>
    [Fact]
    public async Task ShouldIsolateCancellationGivenSharedUnderlyingSubscription()
    {
        // Arrange
        var client = new RecordingStreamClient();
        var store = new FitzEventStore(client, TestJson.DomainSerializer(new DomainEventTypeCatalog()));
        await using var first = await store.SubscribeAsync(Pattern, TestContext.Current.CancellationToken);
        await using var second = await store.SubscribeAsync(Pattern, TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Act
        var cancellation = await Record.ExceptionAsync(
            () => first.WaitAsync(cancelled.Token).AsTask());
        var secondWait = second.WaitAsync(TestContext.Current.CancellationToken).AsTask();
        client.Commit(Pattern.ToString());
        await secondWait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var firstFailure = await Record.ExceptionAsync(() =>
            first.WaitAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // Assert
        _ = Assert.IsAssignableFrom<OperationCanceledException>(cancellation);
        Assert.Null(firstFailure);
        Assert.Equal(1, client.Subscriptions);
        Assert.Equal(0, client.Unsubscribes);
    }

    /// <summary>Parallel logical subscribe and disposal operations preserve one shared source.</summary>
    [Fact]
    public async Task ShouldShareSafelyGivenConcurrentSubscribeAndDispose()
    {
        // Arrange
        var client = new RecordingStreamClient();
        var store = new FitzEventStore(client, TestJson.DomainSerializer(new DomainEventTypeCatalog()));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptions = Enumerable.Range(0, 129)
            .Select(async _ =>
            {
                await start.Task;
                return await store.SubscribeAsync(Pattern, TestContext.Current.CancellationToken);
            })
            .ToArray();

        // Act
        start.SetResult();
        var logical = await Task.WhenAll(subscriptions);
        await Task.WhenAll(logical.Select(subscription => subscription.DisposeAsync().AsTask()));

        // Assert
        Assert.Equal(1, client.Subscriptions);
        Assert.Equal(1, client.Unsubscribes);
    }

    /// <summary>The broker handle remains live until the final logical subscriber is released.</summary>
    [Fact]
    public async Task ShouldReleaseUnderlyingSubscriptionGivenLastLogicalSubscriberDisposed()
    {
        // Arrange
        var client = new RecordingStreamClient();
        var store = new FitzEventStore(client, TestJson.DomainSerializer(new DomainEventTypeCatalog()));
        var first = await store.SubscribeAsync(Pattern, TestContext.Current.CancellationToken);
        var second = await store.SubscribeAsync(Pattern, TestContext.Current.CancellationToken);
        var last = await store.SubscribeAsync(Pattern, TestContext.Current.CancellationToken);

        // Act
        await first.DisposeAsync();
        await second.DisposeAsync();
        var unsubscribesBeforeLast = client.Unsubscribes;
        await last.DisposeAsync();

        // Assert
        Assert.Equal(0, unsubscribesBeforeLast);
        Assert.Equal(1, client.Subscriptions);
        Assert.Equal(1, client.Unsubscribes);
    }

    /// <summary>Backpressure on the shared source reconnects once and wakes every logical subscriber.</summary>
    [Fact]
    public async Task ShouldReconnectOnceAndWakeAllGivenSharedBackpressure()
    {
        // Arrange
        var client = new RecordingStreamClient();
        var store = new FitzEventStore(client, TestJson.DomainSerializer(new DomainEventTypeCatalog()));
        await using var first = await store.SubscribeAsync(Pattern, TestContext.Current.CancellationToken);
        await using var second = await store.SubscribeAsync(Pattern, TestContext.Current.CancellationToken);
        var firstOverflowWait = first.WaitAsync(TestContext.Current.CancellationToken).AsTask();
        var secondOverflowWait = second.WaitAsync(TestContext.Current.CancellationToken).AsTask();

        // Act
        client.Overflow();
        await Task.WhenAll(firstOverflowWait, secondOverflowWait)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var firstCommitWait = first.WaitAsync(TestContext.Current.CancellationToken).AsTask();
        var secondCommitWait = second.WaitAsync(TestContext.Current.CancellationToken).AsTask();
        client.Commit(Pattern.ToString());
        await Task.WhenAll(firstCommitWait, secondCommitWait)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, client.Subscriptions);
        Assert.Equal(1, client.Unsubscribes);
    }

    /// <summary>Shared wait backstops still let every workload fall back to polling durable state.</summary>
    [Fact]
    public async Task ShouldPreservePollingFallbackGivenSharedNotificationPattern()
    {
        // Arrange
        await using var provider = WorkloadLifecycleTestHost.Create();
        var client = new RecordingStreamClient();
        var notifier = new FitzEventStore(client, TestJson.DomainSerializer(new DomainEventTypeCatalog()));
        var firstPolled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPolled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPasses = 0;
        var secondPasses = 0;
        var first = Registration("first", _ =>
        {
            if (Interlocked.Increment(ref firstPasses) == 3)
                firstPolled.TrySetResult();

            return ValueTask.FromResult(default(ProjectionPassResult));
        });
        var second = Registration("second", _ =>
        {
            if (Interlocked.Increment(ref secondPasses) == 3)
                secondPolled.TrySetResult();

            return ValueTask.FromResult(default(ProjectionPassResult));
        });
        using var worker = Worker(provider, [first, second], notifier);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        Exception? processingFailure;

        // Act
        try
        {
            processingFailure = await Record.ExceptionAsync(() =>
                Task.WhenAll(firstPolled.Task, secondPolled.Task)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.Null(processingFailure);
        Assert.True(firstPasses >= 3);
        Assert.True(secondPasses >= 3);
        Assert.Equal(1, client.Subscriptions);
        Assert.Equal(1, client.Unsubscribes);
    }

    static WorkloadRegistration Registration(
        string name,
        Func<CancellationToken, ValueTask<ProjectionPassResult>> pass
    ) =>
        new(new WorkloadLifecycleDescriptor(pass, () => Pattern), name, WorkloadScope.Global, options =>
        {
            options.PollInterval = TimeSpan.FromMilliseconds(10);
            options.FailureAttemptLimit = 1;
        });

    static PortiaWorkloadService Worker(
        ServiceProvider provider,
        IReadOnlyList<WorkloadRegistration> registrations,
        IDomainEventNotifier notifier
    ) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), registrations,
            provider.GetRequiredService<IServiceProviderIsService>(),
            [new ConcurrentWorkloadCoordinator()], null, notifier);

    sealed class ConcurrentWorkloadCoordinator : IWorkloadCoordinator
    {
        public Task RunAsync(
            Func<IReadOnlyCollection<WorkloadIdentity>> workloads,
            Func<WorkloadIdentity, CancellationToken, Task> run,
            CancellationToken ct = default
        ) =>
            Task.WhenAll(workloads().Select(workload => run(workload, ct)));
    }

    sealed class RecordingStreamClient : IStreamClient
    {
        readonly Lock _gate = new();
        readonly List<UnderlyingSubscription> _active = [];
        readonly List<string> _patterns = [];
        long _offset;
        int _subscriptions;
        int _unsubscribes;

        public int Subscriptions => Volatile.Read(ref _subscriptions);
        public int Unsubscribes => Volatile.Read(ref _unsubscribes);

        public string[] Patterns
        {
            get
            {
                lock (_gate)
                    return [.. _patterns];
            }
        }

        public void Commit(string pattern)
        {
            UnderlyingSubscription[] matches;
            lock (_gate)
            {
                matches = [.. _active.Where(subscription => subscription.Pattern == pattern)];
            }

            foreach (var subscription in matches)
            {
                var offset = Interlocked.Increment(ref _offset);
                _ = subscription.Commits.Writer.TryWrite(new StreamCommitEvent(pattern, unchecked((ulong)offset) - 1));
            }
        }

        public void Overflow()
        {
            UnderlyingSubscription[] subscriptions;
            lock (_gate)
                subscriptions = [.. _active];

            foreach (var subscription in subscriptions)
            {
                _ = subscription.Commits.Writer.TryComplete(
                    new SubscriptionBackpressureException("The local subscription buffer is full"));
            }
        }

        public Task<StreamSubscription> SubscribeAsync(string pattern, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var source = new UnderlyingSubscription(pattern);
            lock (_gate)
            {
                _active.Add(source);
                _patterns.Add(pattern);
            }

            _ = Interlocked.Increment(ref _subscriptions);
            return Task.FromResult(new StreamSubscription(
                pattern, ReadAsync(source, ct), token => UnsubscribeAsync(source), Task.CompletedTask));
        }

        public IAsyncEnumerable<StreamReadBatch> ReadAsync(
            string route,
            ulong startOffset,
            StreamReadMode mode,
            StreamReadOptions? options = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<IStreamSession> BeginAsync(
            string route,
            ReadOnlyMemory<byte>? ingestMetadata = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<StreamRecord?> PeekAsync(string route, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StreamMetadata> MetadataAsync(string route, CancellationToken ct = default) =>
            throw new NotSupportedException();

        ValueTask UnsubscribeAsync(UnderlyingSubscription source)
        {
            if (Interlocked.Exchange(ref source.Disposed, 1) != 0)
                return ValueTask.CompletedTask;

            lock (_gate)
                _ = _active.Remove(source);

            _ = Interlocked.Increment(ref _unsubscribes);
            _ = source.Commits.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        static async IAsyncEnumerable<StreamCommitEvent> ReadAsync(
            UnderlyingSubscription source,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            await foreach (var commit in source.Commits.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return commit;
        }

        sealed class UnderlyingSubscription(string pattern)
        {
            public Channel<StreamCommitEvent> Commits { get; } =
                Channel.CreateUnbounded<StreamCommitEvent>();
            public int Disposed;
            public string Pattern { get; } = pattern;
        }
    }
}
