using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

/// <summary>
/// Verifies the tenant control plane end to end: <see cref="EventSourcedTenantDirectory{TStart,TStop}" />
/// reading real tenant lifecycle events off a real <see cref="InMemoryEventStore" />, driving a
/// real <see cref="MultiTenantRunner" /> — not mocked at either layer — so tenants discovered at
/// startup and tenants added or removed live both actually start and stop the right per-tenant
/// work.
/// </summary>
public sealed class MultiTenancyTests
{
    static readonly EventStreamPattern TenantRegistryPattern = EventStreamPattern.ForPattern(area: "tenants", resource: "registry");

    /// <summary>
    /// Verifies that a tenant already registered before the runner starts is picked up at
    /// startup — not missed because it predates the run.
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
            onTenantStarted: async (tenantId, ct) =>
            {
                started.Add(tenantId);
                await WaitForCancellationAsync(ct);
            },
            onTenantStopped: (_, _) => Task.CompletedTask,
            cts.Token);

        await WaitUntilAsync(() => started.Contains(new TenantId("acme")));
        cts.Cancel();
        await AwaitRunAsync(run);

        Assert.Contains(new TenantId("acme"), started);
    }

    /// <summary>
    /// Verifies that a tenant registered after the runner has already started is picked up live,
    /// through <see cref="ITenantDirectory.WatchAsync" />.
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
            onTenantStarted: async (tenantId, ct) =>
            {
                started.Add(tenantId);
                await WaitForCancellationAsync(ct);
            },
            onTenantStopped: (_, _) => Task.CompletedTask,
            cts.Token);

        await RegisterTenantAsync(store, "globex");

        await WaitUntilAsync(() => started.Contains(new TenantId("globex")));
        cts.Cancel();
        await AwaitRunAsync(run);

        Assert.Contains(new TenantId("globex"), started);
    }

    /// <summary>
    /// Verifies that removing a tenant cancels its running work and calls the stop callback —
    /// the per-tenant instance actually stops, not just gets forgotten.
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
            onTenantStarted: async (tenantId, ct) =>
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
            onTenantStopped: (tenantId, _) =>
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
    /// Verifies that cancelling the whole run stops every still-active tenant, calling the stop
    /// callback for each.
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
            onTenantStarted: async (tenantId, ct) =>
            {
                started.Add(tenantId);
                await WaitForCancellationAsync(ct);
            },
            onTenantStopped: (tenantId, _) =>
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

    /// <summary>
    /// Verifies that every active-tenant read is a complete current snapshot, even though the
    /// event-sourced directory advances an internal stream offset between reads. Reconnect
    /// reconciliation depends on unchanged tenants remaining present while newly removed tenants
    /// disappear.
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
    /// Verifies that a transient failure from the tenant directory's live watch stream — a
    /// network blip in a real implementation — doesn't permanently kill tenant management for
    /// the rest of the process. <see cref="MultiTenantRunner" /> must reconnect and keep
    /// discovering tenant changes, the same resilience <see cref="QueueRunner" /> and
    /// <see cref="LiveRequestRunner" /> already have for their own live streams.
    /// </summary>
    [Fact]
    public async Task ShouldReconnectAfterTenantDirectoryWatchStreamFaults()
    {
        var directory = new FlakyWatchTenantDirectory();
        var runner = new MultiTenantRunner(directory);
        var started = new ConcurrentBag<TenantId>();
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(
            onTenantStarted: (tenantId, ct) =>
            {
                started.Add(tenantId);
                return WaitForCancellationAsync(ct);
            },
            onTenantStopped: (_, _) => Task.CompletedTask,
            cts.Token);

        // The first watch stream throws as soon as it's subscribed to; a working runner
        // reconnects and picks up the tenant that arrives on the *second* attempt.
        await WaitUntilAsync(() => started.Contains(new TenantId("recovered-after-reconnect")));

        cts.Cancel();
        await AwaitRunAsync(run);
    }

    /// <summary>
    /// Regression test for a stateful directory (like <see cref="EventSourcedTenantDirectory{TStart,TStop}" />)
    /// whose <c>GetActiveTenantsAsync</c> resumes from its own internal offset rather than a
    /// fresh read: a tenant deregistered during a watch-stream outage never appears as its own
    /// "Removed" change (the offset already moved past it) — it's simply absent from the next
    /// snapshot. A runner that only starts what a reconnect snapshot yields, without reconciling
    /// against what it's still tracking as active, orphans that tenant forever: it keeps running,
    /// with no future change left to ever stop it.
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
            onTenantStarted: (tenantId, ct) =>
            {
                started.Add(tenantId);
                return WaitForCancellationAsync(ct);
            },
            onTenantStopped: (tenantId, _) =>
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
    /// Verifies that a watch stream which keeps completing cleanly (never throwing, never
    /// blocking) — an unusual but real possibility — still gets the same one-second backoff
    /// between reconnect attempts as a genuinely faulting one, rather than spinning as fast as
    /// the directory allows.
    /// </summary>
    [Fact]
    public async Task ShouldBackOffWhenWatchStreamCompletesCleanlyInsteadOfThrowing()
    {
        var directory = new CleanlyCompletingWatchDirectory();
        var runner = new MultiTenantRunner(directory);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(
            onTenantStarted: (_, ct) => WaitForCancellationAsync(ct),
            onTenantStopped: (_, _) => Task.CompletedTask,
            cts.Token);

        // With a one-second backoff, a 250ms window should only ever observe the very first,
        // un-delayed attempt — a spinning loop with no backoff would rack up dozens.
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        cts.Cancel();
        await AwaitRunAsync(run);

        Assert.True(directory.WatchAttempts <= 2, $"Expected at most 2 watch attempts in 250ms with a 1s backoff, got {directory.WatchAttempts}.");
    }

    static EventSourcedTenantDirectory<TenantRegistered, TenantDeregistered> CreateDirectory(InMemoryEventStore store) =>
        new(store, TenantRegistryPattern, GetTenantId, pollInterval: TimeSpan.FromMilliseconds(10));

    static TenantId GetTenantId(DomainEvent ev) => ev switch
    {
        TenantRegistered r => new TenantId(r.TenantId),
        TenantDeregistered d => new TenantId(d.TenantId),
        _ => throw new InvalidOperationException($"Unexpected event type '{ev.GetType()}'."),
    };

    static readonly EventStreamAddress RegistryStream = new("global", "tenants", "registry");

    // AppendAsync is optimistic-concurrency versioned per aggregate stream, and every test in
    // this file appends to the same fixed registry stream address — each test tracks its own
    // running version here rather than assuming a fixed 0/1, since tests appending more than one
    // event (or more than one tenant) would otherwise collide on a hardcoded expected version.
    readonly ConditionalWeakTable<InMemoryEventStore, StrongBox<ulong>> _registryVersions = [];

    async Task RegisterTenantAsync(InMemoryEventStore store, string tenantId) =>
        await AppendAsync(store, new TenantRegistered(tenantId));

    async Task DeregisterTenantAsync(InMemoryEventStore store, string tenantId) =>
        await AppendAsync(store, new TenantDeregistered(tenantId));

    async Task AppendAsync(InMemoryEventStore store, DomainEvent ev)
    {
        var version = _registryVersions.GetOrCreateValue(store);
        ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion7(), Uuid.CreateVersion7(), version.Value + 1, DateTimeOffset.UtcNow));
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
                throw new TimeoutException("Condition was not met in time.");

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
}

sealed record TenantRegistered(string TenantId) : DomainEvent;

sealed record TenantDeregistered(string TenantId) : DomainEvent;

/// <summary>
/// An <see cref="ITenantDirectory" /> whose first <see cref="WatchAsync" /> subscription throws
/// as soon as it's iterated, and whose second attempt yields one tenant and then blocks forever
/// (matching a real, healthy watch stream) — for proving a runner reconnects after a transient
/// failure instead of dying with it.
/// </summary>
sealed class FlakyWatchTenantDirectory : ITenantDirectory
{
    int _watchAttempts;

    public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield break;
    }

    public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var attempt = Interlocked.Increment(ref _watchAttempts);
        await Task.Yield();
        _ = attempt == 1 ? throw new InvalidOperationException("Simulated transient watch stream failure.") : attempt;

        yield return new TenantLifecycleChange(TenantLifecycleChangeKind.Added, new TenantId("recovered-after-reconnect"));

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (ct.Register(() => tcs.TrySetResult()))
            await tcs.Task.ConfigureAwait(false);
    }
}

/// <summary>
/// Models a stateful directory (like <see cref="EventSourcedTenantDirectory{TStart,TStop}" />)
/// across an outage: the first <c>GetActiveTenantsAsync</c> call reports "acme" active, the
/// first <c>WatchAsync</c> subscription then throws, and the *second* <c>GetActiveTenantsAsync</c>
/// call (the reconnect snapshot) reports nothing at all — as if "acme" had been deregistered
/// during the outage and that removal folded silently into the directory's own internal offset,
/// never surfaced as its own change.
/// </summary>
sealed class OutageDuringWhichTenantWasRemovedDirectory : ITenantDirectory
{
    int _snapshotAttempts;
    int _watchAttempts;

    public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var attempt = Interlocked.Increment(ref _snapshotAttempts);
        await Task.Yield();

        if (attempt == 1)
            yield return new TenantId("acme");

        // Second and later snapshots report nothing — "acme" is gone, but never as a change.
    }

    public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var attempt = Interlocked.Increment(ref _watchAttempts);
        await Task.Yield();

        if (attempt == 1)
            throw new InvalidOperationException("Simulated transient watch stream failure.");

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (ct.Register(() => tcs.TrySetResult()))
            await tcs.Task.ConfigureAwait(false);

        yield break;
    }
}

/// <summary>
/// A directory whose <c>WatchAsync</c> subscription always ends cleanly and immediately — never
/// throwing, never blocking — for proving a runner still backs off between reconnect attempts
/// instead of spinning as fast as this allows.
/// </summary>
sealed class CleanlyCompletingWatchDirectory : ITenantDirectory
{
    public int WatchAttempts { get; private set; }

    public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield break;
    }

    public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        WatchAttempts++;
        await Task.Yield();
        yield break;
    }
}
