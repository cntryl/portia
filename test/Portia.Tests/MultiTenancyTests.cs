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

    /// <summary>The runner uses one optional cursor and never mixes it with the legacy contract.</summary>
    [Fact]
    public async Task ShouldPreferOneResumableCursorForTheRunnerLifetime()
    {
        var directory = new ResumableProbeDirectory();
        var clock = new WaitingClock();
        var runner = new MultiTenantRunner(directory, timeProvider: clock);
        var started = new TaskCompletionSource<TenantId>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var run = runner.RunAsync((tenant, token) =>
        {
            _ = started.TrySetResult(tenant);
            return WaitForCancellationAsync(token);
        }, (_, _) => Task.CompletedTask, cancellation.Token);

        Assert.Equal(new TenantId("cursor"), await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(1), await clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, directory.OpenCount);
        Assert.Equal(1, directory.Cursor.ReadCount);
        Assert.Equal(0, directory.SnapshotCount);
        Assert.Equal(0, directory.WatchCount);

        cancellation.Cancel();
        await AwaitRunAsync(run);
    }

    /// <summary>A removal recorded during a cursor outage is applied after the same cursor reconnects.</summary>
    [Fact]
    public async Task ShouldStopTenantRemovedWhileResumableCursorIsDisconnected()
    {
        var directory = new DisconnectingCursorDirectory();
        var clock = new ManualTenantClock();
        var runner = new MultiTenantRunner(directory, timeProvider: clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<TenantId>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var run = runner.RunAsync(async (tenant, token) =>
        {
            _ = tenant;
            _ = started.TrySetResult();
            await WaitForCancellationAsync(token);
        }, (tenant, stopToken) =>
        {
            _ = stopToken;
            _ = stopped.TrySetResult(tenant);
            return Task.CompletedTask;
        }, cancellation.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(1), await clock.WaitForDelayAsync());
        directory.RemovedWhileDisconnected = true;
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new TenantId("acme"), await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, directory.OpenCount);
        Assert.Equal(2, directory.Cursor.ReadCount);

        cancellation.Cancel();
        await AwaitRunAsync(run);
    }

    /// <summary>A cursor resumes its own offset while a separately opened cursor starts independently.</summary>
    [Fact]
    public async Task ResumableCursorContinuesAtNextOffsetAndSeparateCursorsRemainIndependent()
    {
        var store = new InMemoryEventStore();
        await RegisterTenantAsync(store, "acme");
        var directory = CreateDirectory(store);
        await using var first = await directory.OpenCursorAsync();
        await using var second = await directory.OpenCursorAsync();

        Assert.Equal(new TenantLifecycleChange(TenantLifecycleChangeKind.Added, new TenantId("acme")),
            await ReadOneAsync(first));
        Assert.Equal(new TenantLifecycleChange(TenantLifecycleChangeKind.Added, new TenantId("acme")),
            await ReadOneAsync(second));

        await DeregisterTenantAsync(store, "acme");
        Assert.Equal(new TenantLifecycleChange(TenantLifecycleChangeKind.Removed, new TenantId("acme")),
            await ReadOneAsync(first));
        Assert.Equal(new TenantLifecycleChange(TenantLifecycleChangeKind.Removed, new TenantId("acme")),
            await ReadOneAsync(second));
    }

    /// <summary>One cursor cannot be enumerated by two consumers at the same time.</summary>
    [Fact]
    public async Task ResumableCursorRejectsConcurrentEnumeration()
    {
        var clock = new WaitingClock();
        var store = new InMemoryEventStore();
        var directory = new EventSourcedTenantDirectory<TenantRegistered, TenantDeregistered>(store,
            TenantRegistryPattern, GetTenantId, timeProvider: clock);
        await using var cursor = await directory.OpenCursorAsync();
        using var cancellation = new CancellationTokenSource();
        await using var first = cursor.ReadAsync(cancellation.Token).GetAsyncEnumerator();
        var pending = first.MoveNextAsync().AsTask();
        _ = await clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var second = cursor.ReadAsync(cancellation.Token).GetAsyncEnumerator();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => second.MoveNextAsync().AsTask());

        Assert.Equal("A tenant directory cursor supports only one active enumeration.", error.Message);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    /// <summary>A broken cursor read disposes its subscription and resumes after its last durable offset.</summary>
    [Fact]
    public async Task ResumableCursorSubscribesBeforeReadingAndRecreatesAfterFailure()
    {
        var source = new CursorSource();
        source.Add(new TenantRegistered("acme"));
        var directory = new EventSourcedTenantDirectory<TenantRegistered, TenantDeregistered>(source,
            TenantRegistryPattern, GetTenantId, notifier: source);
        await using var cursor = await directory.OpenCursorAsync();
        await using (var first = cursor.ReadAsync().GetAsyncEnumerator())
        {
            Assert.True(await first.MoveNextAsync());
            Assert.Equal(TenantLifecycleChangeKind.Added, first.Current.Kind);
            _ = await Assert.ThrowsAsync<IOException>(() => first.MoveNextAsync().AsTask());
        }

        Assert.Equal(1, source.DisposalCount);
        source.Add(new TenantDeregistered("acme"));
        await using (var resumed = cursor.ReadAsync().GetAsyncEnumerator())
        {
            Assert.True(await resumed.MoveNextAsync());
            Assert.Equal(new TenantLifecycleChange(TenantLifecycleChangeKind.Removed, new TenantId("acme")),
                resumed.Current);
        }

        Assert.Equal([0UL, 1UL, 1UL], source.RequestedOffsets);
        Assert.Equal(2, source.SubscriptionCount);
        Assert.Equal(2, source.DisposalCount);
        Assert.True(source.AllReadsHadSubscription);
    }

    /// <summary>A tenant workload that ignores removal cancellation faults after the configured deadline.</summary>
    [Fact]
    public async Task NormalRemovalFaultsWhenWorkloadIgnoresCancellation()
    {
        var directory = new RemovalDirectory();
        var clock = new ManualTenantClock();
        var timeout = TimeSpan.FromSeconds(5);
        var runner = new MultiTenantRunner(directory, new MultiTenantRunnerOptions
        {
            TenantStopTimeout = timeout
        }, timeProvider: clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = runner.RunAsync((_, _) =>
        {
            _ = started.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
        }, (_, _) => Task.CompletedTask);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        directory.Remove();
        Assert.Equal(timeout, await clock.WaitForDelayAsync());
        clock.Advance(timeout);
        var error = await Assert.ThrowsAsync<TenantStopTimeoutException>(() => run);

        Assert.Equal(new TenantId("acme"), error.TenantId);
        Assert.Equal(timeout, error.Timeout);
    }

    /// <summary>Workload cancellation and its stop callback consume one shared removal budget.</summary>
    [Fact]
    public async Task WorkloadAndStopCallbackShareOneNormalRemovalDeadline()
    {
        var directory = new RemovalDirectory();
        var clock = new ManualTenantClock();
        var options = new MultiTenantRunnerOptions { TenantStopTimeout = TimeSpan.FromSeconds(5) };
        var runner = new MultiTenantRunner(directory, options, timeProvider: clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = runner.RunAsync(async (tenant, token) =>
        {
            _ = tenant;
            _ = started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), clock, CancellationToken.None);
            }
        }, (tenant, token) =>
        {
            _ = tenant;
            _ = stopEntered.TrySetResult(token);
            return Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        directory.Remove();
        Assert.Equal(TimeSpan.FromSeconds(5), await clock.WaitForDelayAsync());
        Assert.Equal(TimeSpan.FromSeconds(3), await clock.WaitForDelayAsync());
        clock.Advance(TimeSpan.FromSeconds(3));
        var stopToken = await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stopToken.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(2));
        _ = await Assert.ThrowsAsync<TenantStopTimeoutException>(() => run);
        Assert.True(stopToken.IsCancellationRequested);
    }

    /// <summary>Host cancellation during normal removal is not reported as a tenant timeout.</summary>
    [Fact]
    public async Task HostCancellationDuringRemovalDoesNotBecomeTenantTimeout()
    {
        var directory = new RemovalDirectory();
        var clock = new ManualTenantClock();
        var runner = new MultiTenantRunner(directory, new MultiTenantRunnerOptions(), timeProvider: clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var run = runner.RunAsync((_, _) =>
        {
            _ = started.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
        }, (_, _) => Task.CompletedTask, cancellation.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        directory.Remove();
        Assert.Equal(TimeSpan.FromSeconds(5), await clock.WaitForDelayAsync());
        cancellation.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Every configurable runner duration must be strictly positive.</summary>
    [Fact]
    public void RunnerOptionsRequireStrictlyPositiveDurations()
    {
        var directory = new RemovalDirectory();
        foreach (var configure in new Action<MultiTenantRunnerOptions>[]
                 {
                     options => options.RestartInterval = TimeSpan.Zero,
                     options => options.ShutdownGrace = TimeSpan.Zero,
                     options => options.TenantStopTimeout = TimeSpan.Zero
                 })
        {
            var options = new MultiTenantRunnerOptions();
            configure(options);
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => new MultiTenantRunner(directory, options));
        }
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

    static async Task<TenantLifecycleChange> ReadOneAsync(ITenantDirectoryCursor cursor)
    {
        await using var reader = cursor.ReadAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        return reader.Current;
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

    sealed class ResumableProbeDirectory : IResumableTenantDirectory
    {
        public ProbeCursor Cursor { get; } = new();
        public int OpenCount { get; private set; }
        public int SnapshotCount { get; private set; }
        public int WatchCount { get; private set; }

        public ValueTask<ITenantDirectoryCursor> OpenCursorAsync(CancellationToken ct = default)
        {
            OpenCount++;
            return ValueTask.FromResult<ITenantDirectoryCursor>(Cursor);
        }

        public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            SnapshotCount++;
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            WatchCount++;
            await Task.CompletedTask;
            yield break;
        }

        public sealed class ProbeCursor : ITenantDirectoryCursor
        {
            public int ReadCount { get; private set; }

            public async IAsyncEnumerable<TenantLifecycleChange> ReadAsync(
                [EnumeratorCancellation] CancellationToken ct = default)
            {
                ReadCount++;
                if (ReadCount == 1)
                    yield return new TenantLifecycleChange(TenantLifecycleChangeKind.Added, new TenantId("cursor"));
                await Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    sealed class RemovalDirectory : IResumableTenantDirectory
    {
        readonly System.Threading.Channels.Channel<TenantLifecycleChange> _changes =
            System.Threading.Channels.Channel.CreateUnbounded<TenantLifecycleChange>();

        public void Remove() => _ = _changes.Writer.TryWrite(
            new TenantLifecycleChange(TenantLifecycleChangeKind.Removed, new TenantId("acme")));

        public ValueTask<ITenantDirectoryCursor> OpenCursorAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<ITenantDirectoryCursor>(new Cursor(_changes.Reader));

        public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        sealed class Cursor(System.Threading.Channels.ChannelReader<TenantLifecycleChange> changes)
            : ITenantDirectoryCursor
        {
            public async IAsyncEnumerable<TenantLifecycleChange> ReadAsync(
                [EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return new TenantLifecycleChange(TenantLifecycleChangeKind.Added, new TenantId("acme"));
                await foreach (var change in changes.ReadAllAsync(ct))
                    yield return change;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    sealed class DisconnectingCursorDirectory : IResumableTenantDirectory
    {
        public ProbeCursor Cursor { get; } = new();
        public int OpenCount { get; private set; }
        public bool RemovedWhileDisconnected { set => Cursor.RemovedWhileDisconnected = value; }

        public ValueTask<ITenantDirectoryCursor> OpenCursorAsync(CancellationToken ct = default)
        {
            OpenCount++;
            return ValueTask.FromResult<ITenantDirectoryCursor>(Cursor);
        }

        public IAsyncEnumerable<TenantId> GetActiveTenantsAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Legacy snapshot must not be used.");

        public IAsyncEnumerable<TenantLifecycleChange> WatchAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Legacy watch must not be used.");

        public sealed class ProbeCursor : ITenantDirectoryCursor
        {
            public int ReadCount { get; private set; }
            public bool RemovedWhileDisconnected { get; set; }

            public async IAsyncEnumerable<TenantLifecycleChange> ReadAsync(
                [EnumeratorCancellation] CancellationToken ct = default)
            {
                ReadCount++;
                if (ReadCount == 1)
                {
                    yield return new TenantLifecycleChange(TenantLifecycleChangeKind.Added, new TenantId("acme"));
                    throw new IOException("Directory disconnected.");
                }

                if (RemovedWhileDisconnected)
                {
                    yield return new TenantLifecycleChange(TenantLifecycleChangeKind.Removed, new TenantId("acme"));
                    RemovedWhileDisconnected = false;
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    sealed class ManualTenantClock : TimeProvider
    {
        readonly Lock _gate = new();
        readonly global::System.Threading.Channels.Channel<TimeSpan> _scheduled =
            global::System.Threading.Channels.Channel.CreateUnbounded<TimeSpan>();
        readonly List<ClockTimer> _timers = [];
        DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state);
            lock (_gate)
                _timers.Add(timer);
            _ = timer.Change(dueTime, period);
            return timer;
        }

        public async Task<TimeSpan> WaitForDelayAsync(CancellationToken ct = default) =>
            await _scheduled.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);

        public void Advance(TimeSpan duration)
        {
            List<ClockTimer> ready;
            lock (_gate)
            {
                _now += duration;
                ready = [.. _timers.Where(timer => timer.Due <= _now)];
                foreach (var timer in ready)
                    timer.Due = timer.Period > TimeSpan.Zero ? _now + timer.Period : DateTimeOffset.MaxValue;
            }

            foreach (var timer in ready)
                timer.Callback(timer.State);
        }

        sealed class ClockTimer(ManualTenantClock clock, TimerCallback callback, object? state) : ITimer
        {
            bool _disposed;
            public TimerCallback Callback => callback;
            public object? State => state;
            public DateTimeOffset Due { get; set; } = DateTimeOffset.MaxValue;
            public TimeSpan Period { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    if (_disposed)
                        return false;
                    Due = dueTime < TimeSpan.Zero ? DateTimeOffset.MaxValue : clock._now + dueTime;
                    Period = period;
                    if (dueTime >= TimeSpan.Zero)
                        _ = clock._scheduled.Writer.TryWrite(dueTime);
                    return true;
                }
            }

            public void Dispose()
            {
                lock (clock._gate)
                {
                    _disposed = true;
                    _ = clock._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    sealed class CursorSource : IDomainEventReader, IDomainEventNotifier
    {
        readonly List<DomainEventRecord> _records = [];
        int _activeSubscriptions;
        int _disposalCount;
        int _failFirstWait = 1;
        int _subscriptionCount;

        public bool AllReadsHadSubscription { get; private set; } = true;
        public int DisposalCount => Volatile.Read(ref _disposalCount);
        public List<ulong> RequestedOffsets { get; } = [];
        public int SubscriptionCount => Volatile.Read(ref _subscriptionCount);

        public void Add(DomainEvent ev)
        {
            var offset = (ulong)_records.Count;
            ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), Uuid.CreateVersion4(), offset + 1,
                DateTimeOffset.UtcNow));
            _records.Add(new DomainEventRecord(RegistryStream, ev, offset, offset, offset));
        }

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset = 0,
            CancellationToken ct = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, ulong fromOffset = 0,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            RequestedOffsets.Add(fromOffset);
            AllReadsHadSubscription &= Volatile.Read(ref _activeSubscriptions) > 0;
            foreach (var record in _records.Where(record => record.ResourceOffset >= fromOffset).ToArray())
            {
                ct.ThrowIfCancellationRequested();
                yield return record;
            }

            await Task.CompletedTask;
        }

        public ValueTask<IDomainEventSubscription> SubscribeAsync(EventStreamPattern pattern,
            CancellationToken ct = default)
        {
            _ = Interlocked.Increment(ref _subscriptionCount);
            _ = Interlocked.Increment(ref _activeSubscriptions);
            return ValueTask.FromResult<IDomainEventSubscription>(new CursorSubscription(this));
        }

        sealed class CursorSubscription(CursorSource owner) : IDomainEventSubscription
        {
            int _disposed;

            public ValueTask WaitAsync(CancellationToken ct = default)
            {
                if (Interlocked.Exchange(ref owner._failFirstWait, 0) != 0)
                    throw new IOException("Cursor watch failed.");
                return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, ct));
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _ = Interlocked.Decrement(ref owner._activeSubscriptions);
                    _ = Interlocked.Increment(ref owner._disposalCount);
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
