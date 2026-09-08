using Cntryl.Fitz.Abstractions.Domains.Queue;
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
        var consumer = new FitzRequestQueueConsumer(new QueueClient([item]), serializer, "queue://consumer/scopes/delivery",
            visibilityTimeoutSeconds: 4, timeProvider: clock);
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

    [Fact]
    public async Task ReservationKeepsRenewingWhileAcknowledgmentIsPending()
    {
        var clock = new ManualClock();
        var serializer = ConsumerJson.CreateSerializer();
        var item = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 1) { BlockCompletion = true };
        var consumer = new FitzRequestQueueConsumer(new QueueClient([item]), serializer, "queue://consumer/scopes/delivery",
            visibilityTimeoutSeconds: 4, timeProvider: clock);
        await using var reader = consumer.ReadAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        _ = await clock.WaitForDelayAsync();
        var complete = reader.Current.CompleteAsync().AsTask();
        await item.CompletionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            await item.Extended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            _ = item.ReleaseCompletion.TrySetResult();
            await complete;
        }
    }

    [Fact]
    public async Task RenewalLossCancelsActiveDeliveryWithoutAcknowledgingAndLaterWorkRuns()
    {
        var clock = new ManualClock();
        var serializer = ConsumerJson.CreateSerializer();
        var failed = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4(), 2)), 6) { FailExtension = true };
        var success = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 1);
        var queue = new QueueClient([failed, success]);
        var services = ConsumerHost.CreateServices();
        _ = services.AddAccounts();
        _ = services.AddScoped<IRequestActorValidator, DeliveryScopeTests.ScopeValidator>();
        _ = services.AddSingleton<IRequestQueueConsumer>(new FitzRequestQueueConsumer(queue, serializer, "queue://consumer/scopes/delivery",
            visibilityTimeoutSeconds: 4, timeProvider: clock));
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
        }
        finally
        {
            cancellation.Cancel();
            try { await run; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task FailingCancellationCallbackDoesNotPreventReservationCleanupOrLaterWork()
    {
        var clock = new ManualClock();
        var serializer = ConsumerJson.CreateSerializer();
        var failed = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 1) { FailExtension = true };
        var success = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 1);
        var consumer = new FitzRequestQueueConsumer(new QueueClient([failed, success]), serializer,
            "queue://consumer/scopes/delivery", visibilityTimeoutSeconds: 4, timeProvider: clock);
        await using var reader = consumer.ReadAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var failingCallback = reader.Current.ReservationCancellation.Register(() => throw new IOException("Application cancellation callback failed"));
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
        var item = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), attempt: 9);
        var queue = new QueueClient([item]);
        var consumer = new FitzRequestQueueConsumer(queue, serializer, "queue://consumer/scopes/delivery");
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
    public async Task MalformedAndUnexpectedFailuresLeaveReservationsForFitzAndLaterWorkRuns()
    {
        var serializer = ConsumerJson.CreateSerializer();
        var malformed = new Reserved("{"u8.ToArray(), 4);
        var failed = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4(), 1)), 6);
        var terminal = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4(), 3)), 1);
        var success = new Reserved(Serialize(serializer, new ScopeRequest(Uuid.CreateVersion4())), 1);
        var queue = new QueueClient([malformed, failed, terminal, success]);
        var services = ConsumerHost.CreateServices();
        _ = services.AddAccounts();
        _ = services.AddScoped<IRequestActorValidator, DeliveryScopeTests.ScopeValidator>();
        _ = services.AddSingleton<IRequestQueueConsumer>(new FitzRequestQueueConsumer(queue, serializer, "queue://consumer/scopes/delivery"));
        _ = services.AddPortiaQueueRunner();
        await using var provider = ConsumerHost.Build(services);
        using var cancellation = new CancellationTokenSource();
        var run = provider.GetRequiredService<QueueRunner>().RunAsync(cancellation.Token);
        try
        {
            var completed = await Task.WhenAny(queue.Idle.Task, run).WaitAsync(TimeSpan.FromSeconds(10));
            await completed;
            Assert.Same(queue.Idle.Task, completed);
            Assert.Equal(0, malformed.Completions);
            Assert.Equal(0, failed.Completions);
            Assert.Equal(1, terminal.Completions);
            Assert.Equal(1, success.Completions);
            Assert.Equal(4U, malformed.Attempt);
            Assert.Equal(6U, failed.Attempt);
            Assert.Equal(0, queue.Enqueues);
        }
        finally
        {
            cancellation.Cancel();
            try { await run; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
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

        public Task<ulong> EnqueueAsync(string route, ReadOnlyMemory<byte> body, int? delayMs = null, CancellationToken ct = default)
        {
            Enqueues++;
            return Task.FromResult(0UL);
        }

        public async Task<IQueueReservedItem[]> ReserveAsync(string route, ulong leaseSeconds, int batchSize = 1, int? waitSeconds = null, CancellationToken ct = default)
        {
            SubscribedBeforeReserve = Subscribed;
            WaitSeconds = waitSeconds;
            BatchSize = batchSize;
            if (Interlocked.Increment(ref _reads) == 1)
                return items;
            _ = Idle.TrySetResult();
            if (waitSeconds == 0)
                return [];
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return [];
        }

        public Task<QueueSubscription> SubscribeAsync(string pattern, CancellationToken ct = default)
        {
            Subscribed = true;
            return Task.FromResult(new QueueSubscription(pattern, Wait(ct), _ => ValueTask.CompletedTask));
        }

        static async IAsyncEnumerable<QueueAvailabilityEvent> Wait(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield break;
        }
    }

    internal sealed class Reserved(ReadOnlyMemory<byte> body, uint attempt) : IQueueReservedItem
    {
        public string Route => "queue://consumer/scopes/delivery";
        public ReadOnlyMemory<byte> Body => body;
        public uint Attempt => attempt;
        public int Completions { get; private set; }
        public int Extensions { get; private set; }
        public TaskCompletionSource Extended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailExtension { get; init; }
        public Task ExtendAsync(ulong leaseSeconds, CancellationToken ct = default)
        {
            Extensions++;
            _ = Extended.TrySetResult();
            return FailExtension ? Task.FromException(new IOException("Reservation renewal disconnected")) : Task.CompletedTask;
        }
        public bool BlockCompletion { get; init; }
        public TaskCompletionSource CompletionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task CompleteAsync(CancellationToken ct = default)
        {
            _ = CompletionStarted.TrySetResult();
            if (BlockCompletion)
                await ReleaseCompletion.Task.WaitAsync(ct);
            Completions++;
        }
        public Task CompleteWithTokenAsync(ulong token, CancellationToken ct = default) => CompleteAsync(ct);
    }
}
