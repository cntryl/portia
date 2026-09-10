namespace Cntryl.Portia.Consumer;

public sealed class TenantDirectoryConsumerTests
{
    [Fact]
    public async Task StreamNotificationWakesIdleDirectoryWithoutPollingDelay()
    {
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Cntryl.Portia.Testing;
            public sealed record Added : DomainEvent;
            public sealed record Removed : DomainEvent;
            public sealed class Reader : IDomainEventReader
            {
                public int Reads;
                public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong offset = 0, CancellationToken ct = default) => throw new NotSupportedException();
                public async IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, ulong offset = 0, [EnumeratorCancellation] CancellationToken ct = default)
                {
                    Reads++;
                    await Task.CompletedTask;
                    yield break;
                }
            }
            public sealed class Changes : IDomainEventNotifier, IDomainEventSubscription
            {
                public TaskCompletionSource Subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                public TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                public ValueTask<IDomainEventSubscription> SubscribeAsync(EventStreamPattern pattern, CancellationToken ct = default)
                { Subscribed.TrySetResult(); return ValueTask.FromResult<IDomainEventSubscription>(this); }
                public ValueTask WaitAsync(CancellationToken ct = default) => new(Changed.Task.WaitAsync(ct));
                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }
            public static class Scenario
            {
                public static async Task<bool> Run()
                {
                    var changes = new Changes();
                    var source = new Reader();
                    var directory = new EventSourcedTenantDirectory<Added, Removed>(source,
                        EventStreamPattern.ForPattern("consumer", "tenants"), _ => new TenantId("acme"), notifier: changes);
                    using var cancellation = new CancellationTokenSource();
                    await using var watch = directory.WatchAsync(cancellation.Token).GetAsyncEnumerator();
                    var move = watch.MoveNextAsync().AsTask();
                    await changes.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    await Task.Delay(50);
                    var passed = source.Reads == 1 && !move.IsCompleted;
                    changes.Changed.TrySetResult();
                    while (source.Reads < 2) await Task.Yield();
                    cancellation.Cancel();
                    try { _ = await move; } catch (OperationCanceledException) { }
                    return passed;
                }
            }
            """);
        Assert.True(await (Task<bool>)assembly.GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WatchersHaveIndependentProgressAcrossOtherWatchersAndSnapshots(bool interveningSnapshot)
    {
        var store = new InMemoryEventStore();
        var directory = CreateDirectory(store);
        var id = Uuid.CreateVersion4();
        using var cancellation = new CancellationTokenSource();
        await using var first = directory.WatchAsync(cancellation.Token).GetAsyncEnumerator();
        await using var second = directory.WatchAsync(cancellation.Token).GetAsyncEnumerator();
        await SeedAsync(store, id, 0, new Activated("acme"));
        Assert.True(await first.MoveNextAsync());
        Assert.Equal(TenantLifecycleChangeKind.Added, first.Current.Kind);
        if (interveningSnapshot)
        {
            await SeedAsync(store, id, 1, new Deactivated("acme"));
            await foreach (var _ in directory.GetActiveTenantsAsync(cancellation.Token)) { }
        }
        var move = second.MoveNextAsync().AsTask();
        try
        {
            Assert.True(await move.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(new TenantId("acme"), second.Current.TenantId);
            Assert.Equal(interveningSnapshot ? TenantLifecycleChangeKind.Removed : TenantLifecycleChangeKind.Added, second.Current.Kind);
        }
        finally
        {
            cancellation.Cancel();
            try { _ = await move; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task InitialReconciliationDoesNotReplayHistoricalStartStopCycles()
    {
        var store = new InMemoryEventStore();
        var clock = new ManualClock();
        var directory = CreateDirectory(store, clock);
        var id = Uuid.CreateVersion4();
        await SeedAsync(store, id, 0, new Activated("acme"));
        await SeedAsync(store, id, 1, new Deactivated("acme"));
        await SeedAsync(store, id, 2, new Activated("acme"));
        using var cancellation = new CancellationTokenSource();
        await using var watch = directory.WatchAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal(new TenantLifecycleChange(TenantLifecycleChangeKind.Added, new TenantId("acme")), watch.Current);
        var next = watch.MoveNextAsync().AsTask();
        try
        {
            _ = await clock.WaitForDelayAsync();
            Assert.False(next.IsCompleted);
            await SeedAsync(store, id, 3, new Deactivated("acme"));
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(TenantLifecycleChangeKind.Removed, watch.Current.Kind);
        }
        finally
        {
            cancellation.Cancel();
            try { _ = await next; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    internal static EventSourcedTenantDirectory<Activated, Deactivated> CreateDirectory(IEventStore store, TimeProvider? clock = null) =>
        new(store, EventStreamPattern.ForPattern("consumer", "tenants"), ev => ev switch
        {
            Activated added => new TenantId(added.Name),
            Deactivated removed => new TenantId(removed.Name),
            _ => throw new InvalidOperationException(),
        }, timeProvider: clock);

    internal static ValueTask SeedAsync(IEventStore store, Uuid id, ulong offset, DomainEvent ev) =>
        store.AppendAsync(new EventStreamAddress("consumer", "tenants", id.ToString()), offset,
            [DomainEventSeed.Attach(ev, id, offset + 1)]);

    public sealed record Activated(string Name) : DomainEvent;
    public sealed record Deactivated(string Name) : DomainEvent;
}
