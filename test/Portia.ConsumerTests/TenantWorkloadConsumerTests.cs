using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

public sealed class TenantWorkloadConsumerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultedWorkloadRestartsUnlessRemovedAndRepeatedAddsDoNotDuplicate(bool removeDuringBackoff)
    {
        var clock = new ManualClock();
        var directory = new Directory();
        var services = ConsumerHost.CreateServices();
        _ = services.AddSingleton<TimeProvider>(clock);
        _ = services.AddSingleton<ITenantDirectory>(directory);
        _ = services.AddSingleton<State>();
        _ = services.AddScoped<Workload>();
        _ = services.AddPortiaMultiTenantRunner<Workload>();
        await using var provider = ConsumerHost.Build(services);
        var worker = Assert.Single(provider.GetServices<IHostedService>());
        var effects = provider.GetRequiredService<ConsumerHost.Effects>();
        using var cancellation = new CancellationTokenSource();
        try
        {
            await worker.StartAsync(default);
            await effects.WaitForAsync("tenant:acme");
            Assert.Equal(TimeSpan.FromSeconds(1), await clock.WaitForDelayAsync(cancellation.Token));
            Assert.True(Assert.Single(effects.Scopes).Value);
            if (!removeDuringBackoff)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await effects.WaitForAsync("tenant:acme", 2);
                await directory.PublishAsync(TenantLifecycleChangeKind.Added);
                await directory.PublishAsync(TenantLifecycleChangeKind.Added);
                Assert.Equal(2, effects.Items.Count);
                Assert.Equal(2, effects.Items.Select(item => item.ScopeId).Distinct().Count());
            }
            await directory.PublishAsync(TenantLifecycleChangeKind.Removed);
            clock.Advance(TimeSpan.FromDays(1));
            Assert.Equal(removeDuringBackoff ? 1 : 2, effects.Items.Count);
            Assert.All(effects.Scopes.Values, Assert.True);
        }
        finally
        {
            cancellation.Cancel();
            await worker.StopAsync(default);
            (worker as IDisposable)?.Dispose();
        }
        Assert.All(effects.Scopes.Values, Assert.True);
    }

    [Fact]
    public async Task TwoHostedWorkloadsSharingDirectoryObserveEveryLifecycleChange()
    {
        var clock = new ManualClock();
        var services = ConsumerHost.CreateServices();
        _ = services.AddSingleton<TimeProvider>(clock);
        _ = services.AddSingleton<ITenantDirectory>(provider => TenantDirectoryConsumerTests.CreateDirectory(provider.GetRequiredService<IEventStore>(), clock));
        _ = services.AddSingleton(new State { FailFirst = false });
        _ = services.AddScoped<Workload>();
        _ = services.AddScoped<SecondWorkload>();
        _ = services.AddPortiaMultiTenantRunner<Workload>();
        _ = services.AddPortiaMultiTenantRunner<SecondWorkload>();
        await using var provider = ConsumerHost.Build(services);
        var workers = provider.GetServices<IHostedService>().ToArray();
        Assert.Equal(2, workers.Length);
        var effects = provider.GetRequiredService<ConsumerHost.Effects>();
        var store = provider.GetRequiredService<IEventStore>();
        var id = Uuid.CreateVersion7();
        try
        {
            foreach (var worker in workers)
                await worker.StartAsync(default);
            _ = await clock.WaitForDelayAsync();
            _ = await clock.WaitForDelayAsync();
            await TenantDirectoryConsumerTests.SeedAsync(store, id, 0, new TenantDirectoryConsumerTests.Activated("globex"));
            clock.Advance(TimeSpan.FromSeconds(1));
            await effects.WaitForAsync("tenant:globex");
            await effects.WaitForAsync("second:globex");
            _ = await clock.WaitForDelayAsync();
            _ = await clock.WaitForDelayAsync();
            await TenantDirectoryConsumerTests.SeedAsync(store, id, 1, new TenantDirectoryConsumerTests.Deactivated("globex"));
            clock.Advance(TimeSpan.FromSeconds(1));
            _ = await clock.WaitForDelayAsync();
            _ = await clock.WaitForDelayAsync();
            Assert.All(effects.Scopes.Values, Assert.True);
            Assert.Equal(2, effects.Items.Count);
        }
        finally
        {
            foreach (var worker in workers)
            {
                await worker.StopAsync(default);
                (worker as IDisposable)?.Dispose();
            }
        }
        Assert.All(effects.Scopes.Values, Assert.True);
    }

    public sealed class SecondWorkload(IConsumerScope scope, IConsumerEffects effects) : ITenantWorkload
    {
        public async Task RunAsync(TenantId tenantId, CancellationToken ct)
        {
            effects.Record("second:" + tenantId.Value, default, 0, scope.Id);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    public sealed class State
    {
        public bool FailFirst { get; init; } = true;
        int _attempts;
        public int NextAttempt() => Interlocked.Increment(ref _attempts);
    }

    public sealed class Workload(State state, IConsumerScope scope, IConsumerEffects effects) : ITenantWorkload
    {
        public async Task RunAsync(TenantId tenantId, CancellationToken ct)
        {
            var attempt = state.NextAttempt();
            effects.Record("tenant:" + tenantId.Value, default, attempt, scope.Id);
            if (state.FailFirst && attempt == 1)
                throw new IOException("Tenant workload failed");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    sealed class Directory : ITenantDirectory
    {
        readonly Channel<(TenantLifecycleChange Change, TaskCompletionSource Done)> _changes = Channel.CreateUnbounded<(TenantLifecycleChange, TaskCompletionSource)>();

        public async Task PublishAsync(TenantLifecycleChangeKind kind)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _changes.Writer.WriteAsync((new TenantLifecycleChange(kind, new TenantId("acme")), done));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }

        public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return new TenantId("acme");
        }

        public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var (change, done) in _changes.Reader.ReadAllAsync(ct))
            {
                yield return change;
                _ = done.TrySetResult();
            }
        }
    }
}
