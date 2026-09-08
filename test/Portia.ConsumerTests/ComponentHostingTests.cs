using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

public sealed class ComponentHostingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamNotificationWakesCompletedWorkloadWithoutPollingDelay(bool projector)
    {
        var changes = new Changes();
        var services = ConsumerHost.CreateServices();
        _ = services.AddSingleton<IDomainEventNotifier>(changes);
        var portia = services.AddPortia();
        _ = (projector
            ? portia.AddProjector<FirstProjector>(WorkloadScope.Global, o => o.PollInterval = TimeSpan.FromDays(1))
            : portia.AddReactor<FirstReactor>(WorkloadScope.Global, o => o.PollInterval = TimeSpan.FromDays(1))).AddWorkers();
        await using var provider = ConsumerHost.Build(services);
        var effects = provider.GetRequiredService<ConsumerHost.Effects>();
        var id = Uuid.CreateVersion4();
        await ConsumerHost.SeedAsync(provider, id);
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        try
        {
            await worker.StartAsync(default);
            await changes.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await effects.WaitForAsync(projector ? "first-projector" : "first-reactor");
            await ConsumerHost.SeedAsync(provider, id, 1);
            changes.Signal();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (effects.Items.Count < 2)
                await Task.Delay(10, timeout.Token);
        }
        finally
        {
            await worker.StopAsync(default);
            (worker as IDisposable)?.Dispose();
        }
        Assert.Equal(2, effects.Items.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("repair")]
    public async Task UncertainProjectionCommitAndFailedReloadPreserveDurableProgress(string? rebuildId)
    {
        var clock = new ManualClock();
        var services = ConsumerHost.CreateServices();
        _ = services.AddSingleton<TimeProvider>(clock);
        _ = services.AddPortia().AddProjector<FirstProjector>(WorkloadScope.Global, o => o.Processing = new ProjectionRunOptions { RebuildId = rebuildId }).AddWorkers();
        await using var provider = ConsumerHost.Build(services);
        var storage = provider.GetRequiredService<ConsumerHost.ProjectionStorage>();
        storage.FailAfterCommit = true;
        var effects = provider.GetRequiredService<ConsumerHost.Effects>();
        await ConsumerHost.SeedAsync(provider, Uuid.CreateVersion4());
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        try
        {
            await worker.StartAsync(default);
            for (var pass = 1; pass <= 3; pass++)
            {
                Assert.Equal(TimeSpan.FromSeconds(1), await clock.WaitForDelayAsync());
                Assert.Equal(pass, storage.LoadAttempts);
                Assert.Equal(rebuildId, Assert.Single(storage.Checkpoints).Key.RebuildId);
                _ = Assert.Single(effects.Items);
                Assert.All(effects.Scopes.Values, Assert.True);
                if (pass < 3)
                    clock.Advance(TimeSpan.FromSeconds(1));
            }
        }
        finally
        {
            await worker.StopAsync(default);
            (worker as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDisposesActiveComponentScope(bool projector)
    {
        var services = ConsumerHost.CreateServices();
        var reader = new BlockingReader();
        _ = services.AddSingleton<IDomainEventReader>(reader);
        var portia = services.AddPortia();
        _ = (projector
            ? portia.AddProjector<FirstProjector>(WorkloadScope.Global)
            : portia.AddReactor<FirstReactor>(WorkloadScope.Global)).AddWorkers();
        await using var provider = ConsumerHost.Build(services);
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        try
        {
            await worker.StartAsync(default);
            await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await worker.StopAsync(default);
            (worker as IDisposable)?.Dispose();
        }
        Assert.True(reader.Disposed);
        Assert.True(Assert.Single(provider.GetRequiredService<ConsumerHost.Effects>().Scopes).Value);
    }

    sealed class BlockingReader : IDomainEventReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset = 0, CancellationToken ct = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, ulong fromOffset = 0,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { Disposed = true; }
            yield break;
        }
    }

    sealed class Changes : IDomainEventNotifier
    {
        readonly System.Threading.Channels.Channel<bool> _signals = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        public TaskCompletionSource Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IDomainEventSubscription> SubscribeAsync(EventStreamPattern pattern, CancellationToken ct = default)
        {
            _ = Subscribed.TrySetResult();
            return ValueTask.FromResult<IDomainEventSubscription>(new Subscription(_signals.Reader));
        }
        public void Signal() => _signals.Writer.TryWrite(true);
        sealed class Subscription(System.Threading.Channels.ChannelReader<bool> signals) : IDomainEventSubscription
        {
            public async ValueTask WaitAsync(CancellationToken ct = default) => _ = await signals.ReadAsync(ct);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PassesDisposeScopesAndReloadDurableProgress(bool projector)
    {
        var clock = new ManualClock();
        var services = ConsumerHost.CreateServices();
        _ = services.AddSingleton<TimeProvider>(clock);
        var portia = services.AddPortia();
        _ = (projector
            ? portia.AddProjector<FirstProjector>(WorkloadScope.Global)
            : portia.AddReactor<FirstReactor>(WorkloadScope.Global)).AddWorkers();
        await using var provider = ConsumerHost.Build(services);
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        var effects = provider.GetRequiredService<ConsumerHost.Effects>();
        var id = Uuid.CreateVersion4();
        await ConsumerHost.SeedAsync(provider, id);
        try
        {
            await worker.StartAsync(default);
            Assert.Equal(TimeSpan.FromSeconds(1), await clock.WaitForDelayAsync());
            _ = Assert.Single(effects.Items);
            Assert.All(effects.Scopes.Values, Assert.True);
            await ConsumerHost.SeedAsync(provider, id, 1);
            clock.Advance(TimeSpan.FromSeconds(1));
            _ = await clock.WaitForDelayAsync();
            Assert.Equal(2, effects.Items.Count);
            Assert.Equal(2, effects.Items.Select(item => item.ScopeId).Distinct().Count());
            Assert.All(effects.Scopes.Values, Assert.True);
            clock.Advance(TimeSpan.FromSeconds(1));
            _ = await clock.WaitForDelayAsync();
            Assert.Equal(2, effects.Items.Count);
        }
        finally
        {
            await worker.StopAsync(default);
            (worker as IDisposable)?.Dispose();
        }
        Assert.All(effects.Scopes.Values, Assert.True);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostsBothConcreteReactorsWithScopes(bool scoped)
    {
        var services = ConsumerHost.CreateServices(scoped);
        _ = services.AddAccounts();
        _ = services.AddReporting();
        _ = services.AddPortia()
            .AddReactor<FirstReactor>(WorkloadScope.Global)
            .AddReactor<SecondReactor>(WorkloadScope.Global)
            .AddWorkers();
        await using var provider = ConsumerHost.Build(services);
        await ConsumerHost.SeedAsync(provider, Uuid.CreateVersion4());
        var workers = provider.GetServices<IHostedService>().OfType<BackgroundService>().ToArray();
        _ = Assert.Single(workers);
        var effects = provider.GetRequiredService<ConsumerHost.Effects>();
        try
        {
            foreach (var worker in workers)
                await worker.StartAsync(default);
            await effects.WaitForAsync("first-reactor");
            await effects.WaitForAsync("second-reactor");
        }
        finally
        {
            foreach (var worker in workers)
            {
                await worker.StopAsync(default);
                (worker as IDisposable)?.Dispose();
            }
        }
        if (scoped)
            Assert.All(effects.Scopes.Values, Assert.True);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostsGeneratedProjectorsSharingOneProjectionPort(bool scoped)
    {
        var services = ConsumerHost.CreateServices(scoped);
        _ = services.AddAccounts();
        _ = services.AddReporting();
        _ = services.AddPortia()
            .AddProjector<FirstProjector>(WorkloadScope.Global)
            .AddProjector<SecondProjector>(WorkloadScope.Global)
            .AddWorkers();
        await using var provider = ConsumerHost.Build(services);
        await ConsumerHost.SeedAsync(provider, Uuid.CreateVersion4());
        var workers = provider.GetServices<IHostedService>().OfType<BackgroundService>().ToArray();
        _ = Assert.Single(workers);
        var effects = provider.GetRequiredService<ConsumerHost.Effects>();
        try
        {
            foreach (var worker in workers)
                await worker.StartAsync(default);
            await effects.WaitForAsync("first-projector");
            await effects.WaitForAsync("second-projector");
        }
        finally
        {
            foreach (var worker in workers)
            {
                await worker.StopAsync(default);
                (worker as IDisposable)?.Dispose();
            }
        }
        if (scoped)
            Assert.All(effects.Scopes.Values, Assert.True);
    }
}
