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
