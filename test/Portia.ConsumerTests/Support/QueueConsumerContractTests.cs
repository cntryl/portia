using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class QueueConsumerContractTests
{
    static ReadOnlyMemory<byte> Serialize(JsonRequestSerializer serializer, ScopeRequest request)
        => serializer.Serialize(request, null, RequestMetadata.Create(), null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveReservationRenewsAndStopsAfterAcknowledgmentOrAbandonment(bool acknowledge)
    {
        var clock = new ManualClock();
        var serializer = ConsumerJson.CreateSerializer();
        var item = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 1);
        var consumer = new FitzRequestQueueConsumer(new QueueClient([item]), serializer,
            "queue://consumer/scopes/delivery",
            ConsumerJson.Catalog(), 4, timeProvider: clock);
        await using var reader = consumer.ReadAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(TimeSpan.FromSeconds(2), await clock.WaitForDelayAsync());
        clock.Advance(TimeSpan.FromSeconds(2));
        await item.Extended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        _ = await clock.WaitForDelayAsync();
        if (acknowledge)
            await reader.Current.CompleteAsync();
        else
            await reader.Current.AbandonAsync();

        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, item.Extensions);
        Assert.Equal(acknowledge ? 1 : 0, item.Completions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettledReservationReleasesItsFitzItem(bool acknowledge)
    {
        var serializer = ConsumerJson.CreateSerializer();
        var item = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 1);
        var consumer = new FitzRequestQueueConsumer(new QueueClient([item]), serializer,
            "queue://consumer/scopes/delivery", ConsumerJson.Catalog(), 4, timeProvider: new ManualClock());
        var reader = consumer.ReadAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        if (acknowledge)
            await reader.Current.CompleteAsync();
        else
            await reader.Current.AbandonAsync();

        await reader.DisposeAsync();

        Assert.Equal(1, item.Disposals);
    }

    // Renewal cannot overlap the acknowledgment against a real Fitz item: COMPLETE moves the item
    // to "completing", after which EXTEND fails locally with ITEM_CLOSED, and an EXTEND already on
    // the wire can reach the broker after COMPLETE retired the lease token. Either way the renewal
    // loop would report a fault and signal a lost reservation for a delivery being acknowledged.
    // So acknowledgment stops renewal first. The last renewal left at least half a lease, and a
    // COMPLETE has to fit inside the lease regardless, since Fitz rejects it once the token expires.
    [Fact]
    public async Task AcknowledgmentStopsRenewalSoItNeverRacesTheCompletion()
    {
        var clock = new ManualClock();
        var serializer = ConsumerJson.CreateSerializer();
        var item = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 1)
        { BlockCompletion = true, CloseOnCompletion = true };
        var consumer = new FitzRequestQueueConsumer(new QueueClient([item]), serializer,
            "queue://consumer/scopes/delivery",
            ConsumerJson.Catalog(), 4, timeProvider: clock);
        await using var reader = consumer.ReadAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        var delivery = reader.Current;
        _ = await clock.WaitForDelayAsync();
        var complete = delivery.CompleteAsync().AsTask();
        await item.CompletionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            clock.Advance(TimeSpan.FromSeconds(2));
        }
        finally
        {
            _ = item.ReleaseCompletion.TrySetResult();
            await complete;
        }

        Assert.Equal(0, item.Extensions);
        Assert.Equal(1, item.Completions);
        Assert.False(delivery.ReservationCancellation.IsCancellationRequested);
    }

    // A wake-up only says "reserve again", so one that arrives while a reserve is already due is
    // redundant. Draining them before every reserve keeps a consumer that never runs dry from
    // filling the bounded subscription buffer, which Fitz ends with SubscriptionBackpressureException.
    [Fact]
    public async Task BusyConsumerDrainsWakeUpsSoTheSubscriptionNeverOverflows()
    {
        var serializer = ConsumerJson.CreateSerializer();
        var queue = new WakingQueueClient(4);
        for (var index = 0; index < 3; index++)
            queue.Enqueue(new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 1));
        var consumer = new FitzRequestQueueConsumer(queue, serializer, "queue://consumer/scopes/delivery",
            ConsumerJson.Catalog(), 4, timeProvider: new ManualClock());
        await using var reader = consumer.ReadAsync().GetAsyncEnumerator();
        for (var round = 0; round < 12; round++)
        {
            Assert.True(await reader.MoveNextAsync());
            await reader.Current.CompleteAsync();
            queue.Enqueue(new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 1));
        }

        await DrainAndWakeAsync(reader, queue, serializer, 3);

        Assert.Equal(1, queue.Subscriptions);
        Assert.Equal(0, queue.Overflows);
    }

    // A burst larger than the buffer can still arrive while one delivery runs. Losing wake-ups is
    // harmless when the next step is a reserve anyway, so the overflow resubscribes and reserves
    // rather than ending the read loop with an error.
    [Fact]
    public async Task WakeUpOverflowResubscribesAndReservesInsteadOfFaulting()
    {
        var serializer = ConsumerJson.CreateSerializer();
        var queue = new WakingQueueClient(2);
        queue.Enqueue(new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 1));
        var consumer = new FitzRequestQueueConsumer(queue, serializer, "queue://consumer/scopes/delivery",
            ConsumerJson.Catalog(), 4, timeProvider: new ManualClock());
        await using var reader = consumer.ReadAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        for (var index = 0; index < 5; index++)
            queue.Enqueue(new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 1));
        await reader.Current.CompleteAsync();

        await DrainAndWakeAsync(reader, queue, serializer, 5);

        Assert.Equal(2, queue.Subscriptions);
        Assert.True(queue.SubscribedBeforeEveryReserve);
    }

    // Consumes the ready items, then proves the consumer is parked on a live subscription: with the
    // backstop clock never advanced, only the next enqueue's wake-up can deliver the following item.
    static async Task DrainAndWakeAsync(IAsyncEnumerator<IQueuedRequest> reader, WakingQueueClient queue,
        JsonRequestSerializer serializer, int ready)
    {
        for (var index = 0; index < ready; index++)
        {
            Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            await reader.Current.CompleteAsync();
        }

        var parked = queue.Parked;
        var next = reader.MoveNextAsync().AsTask();
        await parked.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Enqueue(new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 1));
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        await reader.Current.CompleteAsync();
    }

    [Fact]
    public async Task RenewalLossCancelsActiveDeliveryWithoutAcknowledgingAndLaterWorkRuns()
    {
        var clock = new ManualClock();
        var serializer = ConsumerJson.CreateSerializer();
        var failed = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4(), 2)), 6)
        { FailExtension = true };
        var success = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 1);
        var queue = new QueueClient([failed, success]);
        var services = ConsumerHost.CreateServices();
        _ = services.AddAccounts();
        _ = services.AddScoped<IRequestActorValidator, DeliveryScopeTests.ScopeValidator>();
        _ = services.AddScoped<IQueuedRequestTerminalHandler, TestTerminalHandler>();
        _ = services.AddSingleton<IRequestQueueConsumer>(new FitzRequestQueueConsumer(queue, serializer,
            "queue://consumer/scopes/delivery",
            ConsumerJson.Catalog(), 4, timeProvider: clock));
        _ = services.AddPortiaQueueRunner();
        await using var provider = ConsumerHost.Build(services);
        using var cancellation = new CancellationTokenSource();
        var run = provider.GetRequiredService<QueueRunner>().RunAsync(cancellation.Token);
        try
        {
            await provider.GetRequiredService<ConsumerHost.Effects>().WaitForAsync("nested");
            _ = await clock.WaitForDelayAsync();
            _ = await clock.WaitForDelayAsync();
            clock.Advance(TimeSpan.FromSeconds(2));
            await queue.Idle.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, failed.Completions);
            Assert.Equal(1, success.Completions);
            Assert.Equal(6U, failed.Attempt);
            Assert.Equal(0, queue.Enqueues);
            Assert.All(provider.GetRequiredService<ConsumerHost.Effects>().Scopes.Values, Assert.True);
            Assert.All(provider.GetRequiredService<ConsumerHost.Effects>().Scopes.Values, Assert.True);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await run;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
    }

    [Fact]
    public async Task FailingCancellationCallbackDoesNotPreventReservationCleanupOrLaterWork()
    {
        var clock = new ManualClock();
        var serializer = ConsumerJson.CreateSerializer();
        var failed = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 1)
        { FailExtension = true };
        var success = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 1);
        var consumer = new FitzRequestQueueConsumer(new QueueClient([failed, success]), serializer,
            "queue://consumer/scopes/delivery", ConsumerJson.Catalog(), 4, timeProvider: clock);
        await using var reader = consumer.ReadAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var failingCallback = reader.Current.ReservationCancellation.Register(() =>
            throw new IOException("Application cancellation callback failed"));
        using var observed = reader.Current.ReservationCancellation.Register(lost.SetResult);
        _ = await clock.WaitForDelayAsync();
        _ = await clock.WaitForDelayAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await reader.Current.AbandonAsync();
        Assert.True(await reader.MoveNextAsync());
        await reader.Current.CompleteAsync();
        Assert.Equal(0, failed.Completions);
        Assert.Equal(1, success.Completions);
    }

    [Fact]
    public async Task SubscriptionIsEstablishedBeforeImmediateReserveWithoutChangingAttempt()
    {
        var serializer = ConsumerJson.CreateSerializer();
        var item = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 9);
        var queue = new QueueClient([item]);
        var consumer = new FitzRequestQueueConsumer(queue, serializer, "queue://consumer/scopes/delivery",
            ConsumerJson.Catalog());
        await using var reader = consumer.ReadAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.True(queue.SubscribedBeforeReserve);
        Assert.Equal((0, 1), (queue.WaitSeconds, queue.BatchSize));
        Assert.Equal(9U, reader.Current.Attempt);
        await reader.Current.CompleteAsync();
        Assert.Equal(1, item.Completions);
        Assert.Equal(0, queue.Enqueues);
    }

    [Fact]
    public async Task MalformedUnexpectedAndTerminalFailuresKeepTheirTransportOwnershipRules()
    {
        var serializer = ConsumerJson.CreateSerializer();
        var malformed = new Reserved("{"u8.ToArray(), 4);
        var failed = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4(), 1)), 6);
        var terminal = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4(), 3)), 1);
        var success = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 1);
        var queue = new QueueClient([malformed, failed, terminal, success]);
        var terminalHandler = new TerminalHandler();
        var services = ConsumerHost.CreateServices();
        _ = services.AddAccounts();
        _ = services.AddScoped<IRequestActorValidator, DeliveryScopeTests.ScopeValidator>();
        _ = services.AddSingleton<IRequestQueueConsumer>(new FitzRequestQueueConsumer(queue, serializer,
            "queue://consumer/scopes/delivery", ConsumerJson.Catalog()));
        _ = services.AddSingleton<IQueuedRequestTerminalHandler>(terminalHandler);
        _ = services.AddPortiaQueueRunner();
        await using var provider = ConsumerHost.Build(services);
        using var cancellation = new CancellationTokenSource();
        var run = provider.GetRequiredService<QueueRunner>().RunAsync(cancellation.Token);
        try
        {
            var completed = await Task.WhenAny(queue.Idle.Task, run).WaitAsync(TimeSpan.FromSeconds(10));
            await completed;
            Assert.Same(queue.Idle.Task, completed);
            Assert.Equal(1, malformed.Completions);
            Assert.Equal(0, failed.Completions);
            Assert.Equal(1, terminal.Completions);
            Assert.Equal(1, success.Completions);
            Assert.Collection(terminalHandler.Contexts,
                malformedContext =>
                {
                    Assert.Equal(QueuedRequestTerminalReason.DeserializationFailure, malformedContext.Reason);
                    Assert.Null(malformedContext.Request);
                    Assert.Null(malformedContext.Metadata);
                },
                terminalContext =>
                {
                    Assert.Equal(QueuedRequestTerminalReason.PermanentFailure, terminalContext.Reason);
                    Assert.Equal("Terminal rejection", terminalContext.Error?.Message);
                });
            Assert.Equal(4U, malformed.Attempt);
            Assert.Equal(6U, failed.Attempt);
            Assert.Equal(0, queue.Enqueues);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await run;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
    }

    [Fact]
    public async Task TransportMismatchRetainsEnvelopeForTerminalHandlingAndAcknowledgment()
    {
        var registration = new RequestTransportRegistration(typeof(ScopeRequest),
            [RequestTransportId.Callable],
            new RequestRouteAttribute("consumer", "scopes", "delivery", "run"),
            new DiscriminatorAttribute("consumer.scopes.scope-request"));
        var serializer = new JsonRequestSerializer([registration], ConsumerJson.Options());
        var metadata = RequestMetadata.Create();
        var request = new ScopeRequest(Uuid.CreateVersion4());
        var item = new Reserved(serializer.Serialize(request, null, metadata, null), 1);
        var queue = new QueueClient([item]);
        var terminalHandler = new TerminalHandler();
        var services = ConsumerHost.CreateServices();
        _ = services.AddAccounts();
        _ = services.AddScoped<IRequestActorValidator, DeliveryScopeTests.ScopeValidator>();
        _ = services.AddSingleton<IRequestQueueConsumer>(new FitzRequestQueueConsumer(queue, serializer,
            "queue://consumer/scopes/delivery", new RequestTransportCatalog([registration])));
        _ = services.AddSingleton<IQueuedRequestTerminalHandler>(terminalHandler);
        _ = services.AddPortiaQueueRunner();
        await using var provider = ConsumerHost.Build(services);
        using var cancellation = new CancellationTokenSource();
        var run = provider.GetRequiredService<QueueRunner>().RunAsync(cancellation.Token);
        try
        {
            var completed = await Task.WhenAny(queue.Idle.Task, run).WaitAsync(TimeSpan.FromSeconds(10));
            await completed;

            Assert.Same(queue.Idle.Task, completed);
            var failure = Assert.Single(terminalHandler.Contexts);
            Assert.Equal(QueuedRequestTerminalReason.InvalidTransport, failure.Reason);
            Assert.Equal(request, Assert.IsType<ScopeRequest>(failure.Request));
            Assert.Equal(metadata, failure.Metadata);
            _ = Assert.IsType<InvalidRequestTransportException>(failure.Exception);
            Assert.Equal(1, item.Completions);
            Assert.Empty(provider.GetRequiredService<ConsumerHost.Effects>().Items);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await run;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
    }

    sealed class TerminalHandler : IQueuedRequestTerminalHandler
    {
        public List<QueuedRequestFailureContext> Contexts { get; } = [];

        public ValueTask HandleAsync(QueuedRequestFailureContext context, CancellationToken ct = default)
        {
            Contexts.Add(context);
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class QueueClient(IQueueReservedItem[] items) : IQueueClient
    {
        int _reads;
        public int? WaitSeconds { get; private set; }
        public int BatchSize { get; private set; }
        public int Enqueues { get; private set; }
        public bool Subscribed { get; private set; }
        public bool SubscribedBeforeReserve { get; private set; }
        public TaskCompletionSource Idle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ulong> EnqueueAsync(string route, ReadOnlyMemory<byte> body, TimeSpan? delay = null,
            CancellationToken ct = default)
        {
            Enqueues++;
            return Task.FromResult(0UL);
        }

        public async Task<IQueueReservedItem[]> ReserveAsync(string route, TimeSpan lease, int batchSize = 1,
            TimeSpan? wait = null, CancellationToken ct = default)
        {
            SubscribedBeforeReserve = Subscribed;
            WaitSeconds = wait is null ? null : (int)wait.Value.TotalSeconds;
            BatchSize = batchSize;
            if (Interlocked.Increment(ref _reads) == 1)
                return items;
            _ = Idle.TrySetResult();
            if (wait == TimeSpan.Zero)
            {
                return [];
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return [];
        }

        public Task<QueueSubscription> SubscribeAsync(string pattern, CancellationToken ct = default)
        {
            Subscribed = true;
            return Task.FromResult(new QueueSubscription(pattern, Wait(ct), _ => ValueTask.CompletedTask));
        }

        static async IAsyncEnumerable<QueueAvailabilityEvent> Wait(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield break;
        }
    }

    // Delivers availability notifications through a bounded buffer that ends with
    // SubscriptionBackpressureException on overflow, as Fitz's own queue subscription does.
    internal sealed class WakingQueueClient(int capacity) : IQueueClient
    {
        readonly Lock _gate = new();
        readonly Queue<IQueueReservedItem> _ready = [];
        Channel<QueueAvailabilityEvent>? _notifications;
        TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _overflows;
        int _subscriptions;

        public int Subscriptions => Volatile.Read(ref _subscriptions);
        public int Overflows => Volatile.Read(ref _overflows);
        public bool SubscribedBeforeEveryReserve { get; private set; } = true;

        // Completes at the next reserve that finds the queue empty.
        public Task Parked
        {
            get
            {
                lock (_gate)
                    return _parked.Task;
            }
        }

        public void Enqueue(IQueueReservedItem item)
        {
            Channel<QueueAvailabilityEvent>? notifications;
            lock (_gate)
            {
                _ready.Enqueue(item);
                notifications = _notifications;
            }

            if (notifications is not null &&
                !notifications.Writer.TryWrite(new QueueAvailabilityEvent(item.Route, default)))
            {
                _ = Interlocked.Increment(ref _overflows);
                _ = notifications.Writer.TryComplete(
                    new SubscriptionBackpressureException("The local subscription buffer is full"));
            }
        }

        public Task<ulong> EnqueueAsync(string route, ReadOnlyMemory<byte> body, TimeSpan? delay = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IQueueReservedItem[]> ReserveAsync(string route, TimeSpan lease, int batchSize = 1,
            TimeSpan? wait = null, CancellationToken ct = default)
        {
            lock (_gate)
            {
                SubscribedBeforeEveryReserve &= _notifications is not null;
                if (_ready.TryDequeue(out var item))
                    return Task.FromResult<IQueueReservedItem[]>([item]);
                _ = _parked.TrySetResult();
                _parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return Task.FromResult<IQueueReservedItem[]>([]);
            }
        }

        public Task<QueueSubscription> SubscribeAsync(string pattern, CancellationToken ct = default)
        {
            // Fitz hands a wake-up to a waiting read on the thread pool; handing it over inline keeps
            // the count of wake-ups buffered at each pass deterministic instead of scheduler-bound.
            var notifications = Channel.CreateBounded<QueueAvailabilityEvent>(
                new BoundedChannelOptions(capacity) { AllowSynchronousContinuations = true });
            lock (_gate)
                _notifications = notifications;
            _ = Interlocked.Increment(ref _subscriptions);
            return Task.FromResult(new QueueSubscription(pattern, notifications.Reader.ReadAllAsync(CancellationToken.None),
                _ => ValueTask.CompletedTask));
        }
    }

    internal sealed class Reserved(ReadOnlyMemory<byte> body, uint attempt) : IQueueReservedItem
    {
        public int Completions { get; private set; }
        public int Extensions { get; private set; }
        public TaskCompletionSource Extended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailExtension { get; init; }
        public bool BlockCompletion { get; init; }

        // Mirrors Fitz's QueueReservedItem: once COMPLETE starts, EXTEND fails with ITEM_CLOSED.
        public bool CloseOnCompletion { get; init; }
        bool _closed;

        public TaskCompletionSource CompletionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Route => "queue://consumer/scopes/delivery";
        public ReadOnlyMemory<byte> Body => body;
        public uint Attempt => attempt;

        public Task ExtendAsync(TimeSpan lease, CancellationToken ct = default)
        {
            if (Volatile.Read(ref _closed))
                return Task.FromException(new QueueException("Queue item is completing or closed", "ITEM_CLOSED"));
            Extensions++;
            _ = Extended.TrySetResult();
            return FailExtension
                ? Task.FromException(new IOException("Reservation renewal disconnected"))
                : Task.CompletedTask;
        }

        public async Task CompleteAsync(CancellationToken ct = default)
        {
            if (CloseOnCompletion)
                Volatile.Write(ref _closed, true);
            _ = CompletionStarted.TrySetResult();
            if (BlockCompletion)
                await ReleaseCompletion.Task.WaitAsync(ct);

            Completions++;
        }

        public Task CompleteWithTokenAsync(ulong token, CancellationToken ct = default) => CompleteAsync(ct);

        public int Disposals { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
