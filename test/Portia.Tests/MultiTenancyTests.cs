using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

/// <summary>
///     Verifies the tenant control plane end to end: <see cref="EventSourcedTenantDirectory{TStart,TStop}" />
///     reading real tenant lifecycle events off a real <see cref="InMemoryEventStore" />, driving a
///     real <see cref="MultiTenantRunner" /> — not mocked at either layer — so tenants discovered at
///     startup and tenants added or removed live both actually start and stop the right per-tenant
///     work.
/// </summary>
public sealed class MultiTenancyTests
{
    static readonly EventStreamPattern TenantRegistryPattern =
        EventStreamPattern.ForPattern("global", "tenants", "registry");

    static readonly EventStreamAddress RegistryStream = new("global", "tenants", "registry");

    // AppendAsync is optimistic-concurrency versioned per aggregate stream, and every test in
    // this file appends to the same fixed registry stream address — each test tracks its own
    // running version here rather than assuming a fixed 0/1, since tests appending more than one
    // event (or more than one tenant) would otherwise collide on a hardcoded expected version.
    readonly ConditionalWeakTable<InMemoryEventStore, StrongBox<ulong>> _registryVersions = [];

    /// <summary>
    ///     Verifies that a tenant already registered before the runner starts is picked up at
    ///     startup — not missed because it predates the run.
    /// </summary>
    [Fact]
    public async Task ShouldStartAlreadyRegisteredTenantAtStartup()
    {
        var store = new InMemoryEventStore();
        await RegisterTenantAsync(store, "acme");
        var directory = CreateDirectory(store);
        var runner = new MultiTenantRunner(directory);
        var started = new ConcurrentBag<TenantId>();
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(
            async (tenantId, ct) =>
            {
                started.Add(tenantId);
                await WaitForCancellationAsync(ct);
            },
            (_, _) => Task.CompletedTask,
            cts.Token);

        await WaitUntilAsync(() => started.Contains(new TenantId("acme")));
        cts.Cancel();
        await AwaitRunAsync(run);

        Assert.Contains(new TenantId("acme"), started);
    }

    /// <summary>
    ///     Verifies that a tenant registered after the runner has already started is picked up live,
    ///     through <see cref="ITenantDirectory.WatchAsync" />.
    /// </summary>
    [Fact]
    public async Task ShouldStartTenantRegisteredWhileRunning()
    {
        var store = new InMemoryEventStore();
        var directory = CreateDirectory(store);
        var runner = new MultiTenantRunner(directory);
        var started = new ConcurrentBag<TenantId>();
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(
            async (tenantId, ct) =>
            {
                started.Add(tenantId);
                await WaitForCancellationAsync(ct);
            },
            (_, _) => Task.CompletedTask,
            cts.Token);

        await RegisterTenantAsync(store, "globex");

        await WaitUntilAsync(() => started.Contains(new TenantId("globex")));
        cts.Cancel();
        await AwaitRunAsync(run);

        Assert.Contains(new TenantId("globex"), started);
    }

    /// <summary>
    ///     Verifies that removing a tenant cancels its running work and calls the stop callback —
    ///     the per-tenant instance actually stops, not just gets forgotten.
    /// </summary>
    [Fact]
    public async Task ShouldStopTenantWhenDeregistered()
    {
        var store = new InMemoryEventStore();
        await RegisterTenantAsync(store, "initech");
        var directory = CreateDirectory(store);
        var runner = new MultiTenantRunner(directory);
        var started = new ConcurrentBag<TenantId>();
        var stopped = new ConcurrentBag<TenantId>();
        var observedCancellation = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(
            async (tenantId, ct) =>
            {
                started.Add(tenantId);

                try
                {
                    await WaitForCancellationAsync(ct);
                }
                finally
                {
                    _ = observedCancellation.TrySetResult();
                }
            },
            (tenantId, _) =>
            {
                stopped.Add(tenantId);
                return Task.CompletedTask;
            },
            cts.Token);

        await WaitUntilAsync(() => started.Contains(new TenantId("initech")));
        await DeregisterTenantAsync(store, "initech");

        await WaitUntilAsync(() => stopped.Contains(new TenantId("initech")));
        await observedCancellation.Task;

        cts.Cancel();
        await AwaitRunAsync(run);

        Assert.Contains(new TenantId("initech"), stopped);
    }

    /// <summary>
    ///     Verifies that cancelling the whole run stops every still-active tenant, calling the stop
    ///     callback for each.
    /// </summary>
    [Fact]
    public async Task ShouldStopEveryActiveTenantWhenRunCancelled()
    {
        var store = new InMemoryEventStore();
        await RegisterTenantAsync(store, "a");
        await RegisterTenantAsync(store, "b");
        var directory = CreateDirectory(store);
        var runner = new MultiTenantRunner(directory);
        var started = new ConcurrentBag<TenantId>();
        var stopped = new ConcurrentBag<TenantId>();
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(
            async (tenantId, ct) =>
            {
                started.Add(tenantId);
                await WaitForCancellationAsync(ct);
            },
            (tenantId, _) =>
            {
                stopped.Add(tenantId);
                return Task.CompletedTask;
            },
            cts.Token);

        await WaitUntilAsync(() => started.Count == 2);
        cts.Cancel();
        await AwaitRunAsync(run);

        Assert.Contains(new TenantId("a"), stopped);
        Assert.Contains(new TenantId("b"), stopped);
    }

    /// <summary>Shutdown does not wait forever for an application stop callback that ignores cancellation.</summary>
    [Fact]
    public async Task ShouldBoundTenantStopCallbackByShutdownToken()
    {
        var store = new InMemoryEventStore();
        await RegisterTenantAsync(store, "blocked");
        var runner = new MultiTenantRunner(CreateDirectory(store), shutdownGrace: TimeSpan.FromMilliseconds(50));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(
            async (tenantId, ct) =>
            {
                _ = tenantId;
                _ = started.TrySetResult();
                await WaitForCancellationAsync(ct);
            },
            async (tenantId, stopToken) =>
            {
                _ = tenantId;
                _ = stopToken;
                _ = stopEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
            },
            cts.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stopEntered.Task.IsCompleted);
    }

    /// <summary>Shutdown gives cooperative application cleanup a real bounded grace interval.</summary>
    [Fact]
    public async Task ShouldAllowTenantStopCallbackToCompleteWithinShutdownGrace()
    {
        var store = new InMemoryEventStore();
        await RegisterTenantAsync(store, "cooperative");
        var runner = new MultiTenantRunner(CreateDirectory(store), shutdownGrace: TimeSpan.FromSeconds(1));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCompleted = false;
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(
            async (tenantId, ct) =>
            {
                _ = tenantId;
                _ = started.TrySetResult();
                await WaitForCancellationAsync(ct);
            },
            async (tenantId, stopToken) =>
            {
                _ = tenantId;
                await Task.Delay(TimeSpan.FromMilliseconds(50), stopToken);
                cleanupCompleted = true;
            },
            cts.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cleanupCompleted);
    }

    /// <summary>
    ///     Verifies that every active-tenant read is a complete current snapshot, even though the
    ///     event-sourced directory advances an internal stream offset between reads. Reconnect
    ///     reconciliation depends on unchanged tenants remaining present while newly removed tenants
    ///     disappear.
    /// </summary>
    [Fact]
    public async Task ShouldReturnCompleteActiveSnapshotAcrossRepeatedReads()
    {
        var store = new InMemoryEventStore();
        await RegisterTenantAsync(store, "acme");
        var directory = CreateDirectory(store);

        var first = await ReadActiveTenantsAsync(directory);
        var unchanged = await ReadActiveTenantsAsync(directory);
        await RegisterTenantAsync(store, "globex");
        var added = await ReadActiveTenantsAsync(directory);
        await DeregisterTenantAsync(store, "acme");
        var removed = await ReadActiveTenantsAsync(directory);

        Assert.Equal([new TenantId("acme")], first);
        Assert.Equal([new TenantId("acme")], unchanged);
        Assert.Equal(2, added.Count);
        Assert.Contains(new TenantId("acme"), added);
        Assert.Contains(new TenantId("globex"), added);
        Assert.Equal([new TenantId("globex")], removed);
    }

    /// <summary>
    ///     Verifies that a transient failure from the tenant directory's live watch stream — a
    ///     network blip in a real implementation — doesn't permanently kill tenant management for
    ///     the rest of the process. <see cref="MultiTenantRunner" /> must reconnect and keep
    ///     discovering tenant changes, the same resilience <see cref="QueueRunner" /> and
    ///     <see cref="RequestNotificationRunner" /> already has for its own notification stream.
    /// </summary>
    [Fact]
    public async Task ShouldReconnectAfterTenantDirectoryWatchStreamFaults()
    {
        var directory = new FlakyWatchTenantDirectory();
        var runner = new MultiTenantRunner(directory);
        var started = new ConcurrentBag<TenantId>();
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(
            (tenantId, ct) =>
            {
                started.Add(tenantId);
                return WaitForCancellationAsync(ct);
            },
            (_, _) => Task.CompletedTask,
            cts.Token);

        // The first watch stream throws as soon as it's subscribed to; a working runner
        // reconnects and picks up the tenant that arrives on the *second* attempt.
        await WaitUntilAsync(() => started.Contains(new TenantId("recovered-after-reconnect")));

        cts.Cancel();
        await AwaitRunAsync(run);
    }

    /// <summary>
    ///     Regression test for a stateful directory (like <see cref="EventSourcedTenantDirectory{TStart,TStop}" />)
    ///     whose <c>GetActiveTenantsAsync</c> resumes from its own internal offset rather than a
    ///     fresh read: a tenant deregistered during a watch-stream outage never appears as its own
    ///     "Removed" change (the offset already moved past it) — it's simply absent from the next
    ///     snapshot. A runner that only starts what a reconnect snapshot yields, without reconciling
    ///     against what it's still tracking as active, orphans that tenant forever: it keeps running,
    ///     with no future change left to ever stop it.
    /// </summary>
    [Fact]
    public async Task ShouldStopTenantOmittedFromSnapshotAfterWatchStreamOutage()
    {
        var directory = new OutageDuringWhichTenantWasRemovedDirectory();
        var runner = new MultiTenantRunner(directory);
        var started = new ConcurrentBag<TenantId>();
        var stopped = new ConcurrentBag<TenantId>();
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(
            (tenantId, ct) =>
            {
                started.Add(tenantId);
                return WaitForCancellationAsync(ct);
            },
            (tenantId, _) =>
            {
                stopped.Add(tenantId);
                return Task.CompletedTask;
            },
            cts.Token);

        // "acme" starts at startup, the watch stream then fails, and the reconnect snapshot no
        // longer reports "acme" at all (simulating it being deregistered during the outage and
        // folded into the directory's own offset advance) — a correct runner stops it.
        await WaitUntilAsync(() => stopped.Contains(new TenantId("acme")));

        cts.Cancel();
        await AwaitRunAsync(run);

        Assert.Contains(new TenantId("acme"), started);
        Assert.Contains(new TenantId("acme"), stopped);
    }

    /// <summary>
    ///     Verifies that a watch stream which keeps completing cleanly (never throwing, never
    ///     blocking) — an unusual but real possibility — still gets the same one-second backoff
    ///     between reconnect attempts as a genuinely faulting one, rather than spinning as fast as
    ///     the directory allows.
    /// </summary>
    [Fact]
    public async Task ShouldBackOffWhenWatchStreamCompletesCleanlyInsteadOfThrowing()
    {
        var directory = new CleanlyCompletingWatchDirectory();
        var clock = new WaitingClock();
        var runner = new MultiTenantRunner(directory, timeProvider: clock);
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(
            (_, ct) => WaitForCancellationAsync(ct),
            (_, _) => Task.CompletedTask,
            cts.Token);
        Assert.Equal(TimeSpan.FromSeconds(1), await clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        cts.Cancel();
        await AwaitRunAsync(run);
        Assert.Equal(1, directory.WatchAttempts);
    }

    static EventSourcedTenantDirectory<TenantRegistered, TenantDeregistered>
        CreateDirectory(InMemoryEventStore store) =>
        new(store, TenantRegistryPattern, GetTenantId, TimeSpan.FromMilliseconds(10));

    static TenantId GetTenantId(DomainEvent ev) => ev switch
    {
        TenantRegistered r => new TenantId(r.TenantId),
        TenantDeregistered d => new TenantId(d.TenantId),
        _ => throw new InvalidOperationException($"Unexpected event type '{ev.GetType()}'.")
    };

    async Task RegisterTenantAsync(InMemoryEventStore store, string tenantId) =>
        await AppendAsync(store, new TenantRegistered(tenantId));

    async Task DeregisterTenantAsync(InMemoryEventStore store, string tenantId) =>
        await AppendAsync(store, new TenantDeregistered(tenantId));

    async Task AppendAsync(InMemoryEventStore store, DomainEvent ev)
    {
        var version = _registryVersions.GetOrCreateValue(store);
        ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), Uuid.CreateVersion4(), version.Value + 1,
            DateTimeOffset.UtcNow));
        await store.AppendAsync(RegistryStream, version.Value, [ev]);
        version.Value++;
    }

    static Task WaitForCancellationAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource();
        _ = ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    static async Task<List<TenantId>> ReadActiveTenantsAsync(
        EventSourcedTenantDirectory<TenantRegistered, TenantDeregistered> directory)
    {
        var tenants = new List<TenantId>();

        await foreach (var tenantId in directory.GetActiveTenantsAsync())
            tenants.Add(tenantId);

        return tenants;
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(10);
        }
    }

    static async Task AwaitRunAsync(Task run)
    {
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // Expected once the driving CancellationTokenSource is cancelled.
        }
    }

    sealed class WaitingClock : TimeProvider
    {
        public TaskCompletionSource<TimeSpan> Scheduled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _ = Scheduled.TrySetResult(dueTime);
            return new WaitingTimer();
        }

        sealed class WaitingTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
