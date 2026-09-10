using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

public sealed partial class ComponentHostingTests
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
                : portia.AddReactor<FirstReactor>(WorkloadScope.Global, o => o.PollInterval = TimeSpan.FromDays(1)))
            .AddWorkers();
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
        Assert.Equal(2, effects.Items.Select(item => item.ScopeId).Distinct().Count());
        Assert.Equal(1, changes.SubscriptionCount);
        Assert.Equal(1, changes.DisposalCount);
    }

    [Fact]
    public async Task BrokenNotificationSubscriptionIsDisposedAndRecreatedBeforeNextPass()
    {
        var clock = new ManualClock();
        var changes = new Changes { FailNextWait = true };
        var services = ConsumerHost.CreateServices();
        _ = services.AddSingleton<TimeProvider>(clock);
        _ = services.AddSingleton<IDomainEventNotifier>(changes);
        _ = services.AddPortia().AddProjector<FirstProjector>(WorkloadScope.Global,
            options => options.PollInterval = TimeSpan.FromSeconds(1)).AddWorkers();
        await using var provider = ConsumerHost.Build(services);
        await ConsumerHost.SeedAsync(provider, Uuid.CreateVersion4());
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        try
        {
            await worker.StartAsync(default);
            Assert.Equal(TimeSpan.FromSeconds(1), await clock.WaitForDelayAsync());
            Assert.Equal(1, changes.SubscriptionCount);
            Assert.Equal(1, changes.DisposalCount);
            clock.Advance(TimeSpan.FromSeconds(1));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (changes.SubscriptionCount < 2)
                await Task.Delay(10, timeout.Token);
        }
        finally
        {
            await worker.StopAsync(default);
            (worker as IDisposable)?.Dispose();
        }

        Assert.Equal(2, changes.SubscriptionCount);
        Assert.Equal(2, changes.DisposalCount);
    }

    [Fact]
    public async Task ScopedComponentCannotChangeTheRetainedSubscriptionPattern()
    {
        var changes = new Changes();
        var services = ConsumerHost.CreateServices();
        _ = services.AddSingleton<IDomainEventNotifier>(changes);
        _ = services.AddSingleton<PatternSequence>();
        _ = services.AddPortia().AddProjector<ChangingPatternProjector>(WorkloadScope.Global, options =>
        {
            options.FailureAttemptLimit = 1;
            options.PollInterval = TimeSpan.FromDays(1);
        }).AddWorkers();
        await using var provider = ConsumerHost.Build(services);
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        await worker.StartAsync(default);
        await changes.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        changes.Signal();

        var failure = await Assert.ThrowsAsync<WorkloadFailureException>(async () =>
            await (worker.ExecuteTask ?? throw new InvalidOperationException("The worker did not start.")));

        Assert.Contains("changed its event-stream pattern", failure.InnerException?.Message,
            StringComparison.Ordinal);
        Assert.Equal(1, changes.SubscriptionCount);
        Assert.Equal(1, changes.DisposalCount);
        worker.Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("repair")]
    public async Task UncertainProjectionCommitAndFailedReloadPreserveDurableProgress(string? rebuildId)
    {
        var clock = new ManualClock();
        var services = ConsumerHost.CreateServices();
        _ = services.AddSingleton<TimeProvider>(clock);
        _ = services.AddPortia().AddProjector<FirstProjector>(WorkloadScope.Global,
            o => o.Processing = new ProjectionRunOptions { RebuildId = rebuildId }).AddWorkers();
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
                Assert.Equal(TimeSpan.FromSeconds(pass == 2 ? 2 : 1), await clock.WaitForDelayAsync());
                Assert.Equal(pass, storage.LoadAttempts);
                Assert.Equal(rebuildId, Assert.Single(storage.Checkpoints).Key.RebuildId);
                _ = Assert.Single(effects.Items);
                Assert.All(effects.Scopes.Values, Assert.True);
                if (pass < 3)
                {
                    clock.Advance(TimeSpan.FromSeconds(pass));
                }
            }
        }
        finally
        {
            await worker.StopAsync(default);
            (worker as IDisposable)?.Dispose();
        }

    }

    [Fact]
    public async Task PoisonEventBacksOffThenFaultsWorkerAtAttemptLimit()
    {
        var clock = new ManualClock();
        var services = ConsumerHost.CreateServices();
        _ = services.AddSingleton<TimeProvider>(clock);
        var changes = new Changes();
        _ = services.AddSingleton<IDomainEventNotifier>(changes);
        _ = services.AddPortia().AddProjector<FirstProjector>(WorkloadScope.Global, options =>
        {
            options.FailureAttemptLimit = 2;
            options.PollInterval = TimeSpan.FromSeconds(1);
        }).AddWorkers();
        await using var provider = ConsumerHost.Build(services);
        provider.GetRequiredService<ConsumerHost.ProjectionStorage>().FailAfterCommit = true;
        await ConsumerHost.SeedAsync(provider, Uuid.CreateVersion4());
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());

        await worker.StartAsync(default);
        Assert.Equal(TimeSpan.FromSeconds(1), await clock.WaitForDelayAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        var failure = await Assert.ThrowsAsync<WorkloadFailureException>(async () =>
            await (worker.ExecuteTask ?? throw new InvalidOperationException("The worker did not start.")));

        Assert.Equal(2, failure.Attempts);
        _ = Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.Equal(1, changes.SubscriptionCount);
        Assert.Equal(1, changes.DisposalCount);
        (worker as IDisposable)?.Dispose();
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
        {
            Assert.All(effects.Scopes.Values, Assert.True);
        }
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
        {
            Assert.All(effects.Scopes.Values, Assert.True);
        }
    }

    sealed class BlockingReader : IDomainEventReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset = 0,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, ulong fromOffset = 0,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                Disposed = true;
            }

            yield break;
        }
    }

    sealed class Changes : IDomainEventNotifier
    {
        readonly Channel<bool> _signals = Channel.CreateUnbounded<bool>();
        int _disposalCount;
        int _failNextWait;
        int _subscriptionCount;
        public TaskCompletionSource Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposalCount => Volatile.Read(ref _disposalCount);
        public bool FailNextWait { set => Volatile.Write(ref _failNextWait, value ? 1 : 0); }
        public int SubscriptionCount => Volatile.Read(ref _subscriptionCount);

        public ValueTask<IDomainEventSubscription> SubscribeAsync(EventStreamPattern pattern,
            CancellationToken ct = default)
        {
            _ = Interlocked.Increment(ref _subscriptionCount);
            _ = Subscribed.TrySetResult();
            return ValueTask.FromResult<IDomainEventSubscription>(new Subscription(this, _signals.Reader));
        }

        public void Signal() => _signals.Writer.TryWrite(true);

        sealed class Subscription(Changes owner, ChannelReader<bool> signals) : IDomainEventSubscription
        {
            int _disposed;

            public async ValueTask WaitAsync(CancellationToken ct = default)
            {
                if (Interlocked.Exchange(ref owner._failNextWait, 0) != 0)
                {
                    throw new InvalidOperationException("Notification wait failed.");
                }

                _ = await signals.ReadAsync(ct);
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _ = Interlocked.Increment(ref owner._disposalCount);
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    internal sealed class PatternSequence
    {
        int _created;

        public EventStreamPattern Next() => EventStreamPattern.ForPattern(
            Interlocked.Increment(ref _created) == 1 ? "first-pattern" : "second-pattern");
    }

    internal sealed partial class ChangingPatternProjector(IAccountRepository target, PatternSequence patterns)
        : Projector(target, patterns.Next(), "changing-pattern"), IProjectorHandler<Deposited>
    {
        public ValueTask HandleAsync(Deposited ev, IProjectorContext context, CancellationToken ct) =>
            ValueTask.CompletedTask;
    }
}
