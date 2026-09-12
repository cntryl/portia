using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

public sealed class FleetRedistributionTests
{
    static readonly string[] Partitions = [.. Enumerable.Range(0, 8).Select(i => $"lease://fleet/parts/{i}")];

    static FleetRunOptions Options => new()
    {
        MembershipSelector = "lease://fleet/members/*",
        WorkerId = "a",
        ReconciliationInterval = TimeSpan.FromMilliseconds(10)
    };

    [Fact]
    public async Task JoinMovesOnlyNewOwnersAndPreservesUnchangedScopesThenDepartureRestoresWork()
    {
        var membership = new Membership();
        membership.Observer.Set("a");
        var state = new State();
        var services = new ServiceCollection();
        _ = services.AddSingleton<IFleetMembership>(membership);
        _ = services.AddSingleton<IPartitionLeaseCompetitor, InMemoryLeaseClient>();
        _ = services.AddSingleton(state);
        _ = services.AddScoped<Workload>();
        _ = services.AddPortiaFleetPartitionRunner<Workload>(Partitions, Options);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        { ValidateScopes = true, ValidateOnBuild = true });
        var host = Assert.Single(provider.GetServices<IHostedService>());
        try
        {
            await host.StartAsync(default);
            await Until(() => state.Active.Count == 8);
            var original = state.Active.ToDictionary();
            membership.Observer.Set("b", "a");
            await Until(() => state.Active.Count == 4);
            Assert.Equal([Partitions[2], Partitions[3], Partitions[5], Partitions[7]],
                state.Active.Keys.Order(StringComparer.Ordinal));
            foreach (var (route, scope) in state.Active)
                Assert.Equal(original[route], scope);
            Assert.Equal(4, state.Disposed.Count);
            membership.Observer.Set("a", "b", "c");
            await Until(() => state.Active.Count == 3);
            Assert.False(state.Active.ContainsKey(Partitions[5]));
            membership.Observer.Set("c", "b", "a");
            await Task.Delay(50);
            Assert.Equal(8, state.Starts);
            membership.Observer.Set("a");
            await Until(() => state.Active.Count == 8);
            Assert.Equal(13, state.Starts);
        }
        finally
        {
            await host.StopAsync(default);
            (host as IDisposable)?.Dispose();
        }

        Assert.Empty(state.Active);
        Assert.Equal(state.Starts, state.Disposed.Count);
    }

    [Fact]
    public async Task UnreadyOrMissingMembershipStopsWorkAndReadySnapshotResumes()
    {
        var membership = new Membership();
        membership.Observer.Set("a");
        var active = 0;
        using var cancellation = new CancellationTokenSource();
        var run = new FleetPartitionRunner(new InMemoryLeaseClient(), membership).RunAsync(Partitions,
            async (partition, ct) =>
            {
                _ = partition;
                _ = Interlocked.Increment(ref active);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                finally
                {
                    _ = Interlocked.Decrement(ref active);
                }
            }, Options, cancellation.Token);
        try
        {
            await Until(() => active == 8);
            membership.Observer.IsReady = false;
            await Until(() => active == 0);
            membership.Observer.IsReady = true;
            await Until(() => active == 8);
            membership.Observer.Set("b");
            await Until(() => active == 0);
            membership.Observer.Set("a");
            await Until(() => active == 8);
        }
        finally
        {
            cancellation.Cancel();
            await run;
        }
    }

    [Theory]
    [InlineData("lease://fleet/parts/*", null, 30)]
    [InlineData("lease://fleet/*/*", null, 30)]
    [InlineData("lease://fleet/members/a", null, 30)]
    [InlineData("lease://fleet/members/*", "", 30)]
    [InlineData("lease://fleet/members/*", "bad/id", 30)]
    [InlineData("lease://fleet/members/*", null, 0)]
    public void InvalidFleetConfigurationDoesNotMutateServices(string selector, string? workerId, int ttl)
    {
        var services = new ServiceCollection();
        _ = Assert.ThrowsAny<ArgumentException>(() => services.AddPortiaFleetPartitionRunner<Workload>(Partitions,
            Options with { MembershipSelector = selector, WorkerId = workerId, LeaseTtl = TimeSpan.FromSeconds(ttl) }));
        Assert.Empty(services);
    }

    [Theory]
    [InlineData(30_000, 30UL)]
    [InlineData(1100, 2UL)]
    [InlineData(1, 1UL)]
    public async Task MembershipFailureBackoffRetainsGeneratedWorkerIdentityAndUsesFailFastContention(
        int milliseconds,
        ulong expectedTtl)
    {
        var clock = new ManualClock();
        var membership = new Membership { FailFirst = true, AutoSelf = true };
        var leases = new CapturingLeases();
        using var cancellation = new CancellationTokenSource();
        var run = new FleetPartitionRunner(leases, membership, timeProvider: clock).RunAsync([Partitions[0]],
            (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct),
            Options with { WorkerId = null, LeaseTtl = TimeSpan.FromMilliseconds(milliseconds) }, cancellation.Token);
        try
        {
            // The retry delay is chosen by the runner's backoff policy, so the test observes it
            // instead of restating it: what matters is that the retry waits for it and no longer.
            var retry = await clock.WaitForDelayAsync();
            Assert.InRange(retry, TimeSpan.Zero, FleetPartitionRunner.MaximumRetryBackoff);
            _ = Assert.Single(membership.Attempts);
            clock.Advance(retry - TimeSpan.FromTicks(1));
            _ = Assert.Single(membership.Attempts);
            clock.Advance(TimeSpan.FromTicks(1));
            await Until(() => leases.Ttl != 0);
            Assert.Equal(expectedTtl, leases.Ttl);
            Assert.False(leases.Options?.WaitForAvailability);
            Assert.Equal(2, membership.Attempts.Count);
            var workerIds = membership.Attempts.Select(options => options.WorkerId).ToArray();
            Assert.Equal(workerIds[0], workerIds[1]);
            Assert.Equal(4, Guid.Parse(workerIds[0]!).Version);
        }
        finally
        {
            cancellation.Cancel();
            await run;
        }
    }

    [Fact]
    public async Task InventoryFailureStopsActiveWorkBeforeMembershipRetry()
    {
        var clock = new ManualClock();
        var membership = new Membership { AutoSelf = true };
        var active = 0;
        using var cancellation = new CancellationTokenSource();
        var run = new FleetPartitionRunner(new InMemoryLeaseClient(), membership, timeProvider: clock).RunAsync(
            Partitions,
            async (partition, ct) =>
            {
                _ = partition;
                _ = Interlocked.Increment(ref active);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                finally
                {
                    _ = Interlocked.Decrement(ref active);
                }
            }, Options, cancellation.Token);
        try
        {
            _ = await clock.WaitForDelayAsync();
            await Until(() => active == 8);
            membership.Observer.FailView = true;
            clock.Advance(Options.ReconciliationInterval);
            // The point of the test is the ordering — work stops, and only then is membership
            // retried — so it asserts on that state rather than on which timer fires first.
            await Until(() => active == 0);
            _ = Assert.Single(membership.Attempts);
            membership.Observer.FailView = false;
            await UntilAdvanced(clock, () => membership.Attempts.Count == 2);
            await Until(() => active == 8);
        }
        finally
        {
            cancellation.Cancel();
            await run;
        }

        Assert.Equal(0, active);
    }

    // The retry delay belongs to the runner's backoff policy, so a test waiting on a retry drives
    // the clock past the policy's ceiling instead of restating whatever delay it happened to pick.
    internal static async Task UntilAdvanced(ManualClock clock, Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!predicate())
        {
            clock.Advance(FleetPartitionRunner.MaximumRetryBackoff);
            await Task.Delay(5, deadline.Token);
        }
    }

    internal static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!predicate())
            await Task.Delay(5, deadline.Token);
    }

    sealed class CapturingLeases : IPartitionLeaseCompetitor
    {
        public ulong Ttl { get; private set; }
        public LeaseExecutionOptions? Options { get; private set; }

        public async Task WithLeaseAsync(string route, ulong ttlSecs, Func<CancellationToken, ValueTask> callback,
            LeaseExecutionOptions? options = null, CancellationToken ct = default)
        {
            Ttl = ttlSecs;
            Options = options;
            await callback(ct);
        }
    }

    public sealed class State
    {
        int _starts;
        public ConcurrentDictionary<string, Guid> Active { get; } = new();
        public ConcurrentBag<Guid> Disposed { get; } = [];
        public int Starts => Volatile.Read(ref _starts);
        public void Started() => Interlocked.Increment(ref _starts);
    }

    public sealed class Workload(State state) : IPartitionWorkload, IDisposable
    {
        readonly Guid _id = Guid.NewGuid();
        public void Dispose() => state.Disposed.Add(_id);

        public async Task RunAsync(string partition, CancellationToken ct)
        {
            state.Started();
            Assert.True(state.Active.TryAdd(partition, _id));
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                _ = state.Active.TryRemove(partition, out _);
            }
        }
    }

    sealed class Membership : IFleetMembership
    {
        public Observer Observer { get; } = new();
        public bool FailFirst { get; init; }
        public bool AutoSelf { get; init; }
        public ConcurrentQueue<FleetRunOptions> Attempts { get; } = new();

        public Task RunAsync(FleetRunOptions options, Func<ILeaseInventoryObserver, CancellationToken, Task> callback,
            CancellationToken ct = default)
        {
            Attempts.Enqueue(options);
            if (FailFirst && Attempts.Count == 1)
            {
                throw new IOException("Membership unavailable");
            }

            if (AutoSelf)
            {
                Observer.Set(options.WorkerId!);
            }

            return callback(Observer, ct);
        }
    }

    sealed class Observer : ILeaseInventoryObserver
    {
        volatile bool _ready = true;
        volatile IReadOnlyDictionary<string, LeaseListItem> _view = new Dictionary<string, LeaseListItem>();
        public bool FailView { get; set; }

        public bool IsReady
        {
            get => _ready;
            set => _ready = value;
        }

        public IReadOnlyDictionary<string, LeaseListItem> View =>
            FailView ? throw new IOException("Inventory failed") : _view;

        public IAsyncEnumerable<LeaseInventoryUpdate> Updates => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Set(params string[] workers) => _view = workers.ToDictionary(
            worker => "lease://fleet/members/" + worker,
            worker => new LeaseListItem("lease://fleet/members/" + worker, "owner", 1, "",
                TimeSpan.FromSeconds(30), 0));
    }
}
