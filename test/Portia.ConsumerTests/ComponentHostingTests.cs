using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

public sealed class ComponentHostingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("repair")]
    public async Task UncertainProjectionCommitAndFailedReloadPreserveDurableProgress(string? rebuildId)
    {
        var clock = new ManualClock();
        var services = ConsumerHost.CreateServices();
        _ = services.AddSingleton<TimeProvider>(clock);
        _ = services.AddPortiaModule<AccountsModule>();
        _ = services.AddPortiaProjectorRunner<FirstProjector>(new ProjectionRunOptions { RebuildId = rebuildId });
        await using var provider = ConsumerHost.Build(services);
        var storage = provider.GetRequiredService<ConsumerHost.ProjectionStorage>();
        storage.FailAfterCommit = true;
        var effects = provider.GetRequiredService<ConsumerHost.Effects>();
        await ConsumerHost.SeedAsync(provider, Uuid.CreateVersion4());
        var worker = Assert.Single(provider.GetServices<IHostedService>());
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
        _ = services.AddPortiaModule<AccountsModule>();
        _ = projector ? services.AddPortiaProjectorRunner<FirstProjector>() : services.AddPortiaReactorRunner<FirstReactor>();
        await using var provider = ConsumerHost.Build(services);
        var worker = Assert.Single(provider.GetServices<IHostedService>());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PassesDisposeScopesAndReloadDurableProgress(bool projector)
    {
        var clock = new ManualClock();
        var services = ConsumerHost.CreateServices();
        _ = services.AddSingleton<TimeProvider>(clock);
        _ = services.AddPortiaModule<AccountsModule>();
        _ = projector ? services.AddPortiaProjectorRunner<FirstProjector>() : services.AddPortiaReactorRunner<FirstReactor>();
        await using var provider = ConsumerHost.Build(services);
        var worker = Assert.Single(provider.GetServices<IHostedService>());
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
        _ = services.AddPortiaModule<AccountsModule>();
        _ = services.AddPortiaModule<ReportingModule>();
        _ = services.AddPortiaReactorRunner<FirstReactor>();
        _ = services.AddPortiaReactorRunner<SecondReactor>();
        await using var provider = ConsumerHost.Build(services);
        await ConsumerHost.SeedAsync(provider, Uuid.CreateVersion4());
        var workers = provider.GetServices<IHostedService>().ToArray();
        Assert.Equal(2, workers.Length);
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
        _ = services.AddPortiaModule<AccountsModule>();
        _ = services.AddPortiaModule<ReportingModule>();
        _ = services.AddPortiaProjectorRunner<FirstProjector>();
        _ = services.AddPortiaProjectorRunner<SecondProjector>();
        await using var provider = ConsumerHost.Build(services);
        await ConsumerHost.SeedAsync(provider, Uuid.CreateVersion4());
        var workers = provider.GetServices<IHostedService>().ToArray();
        Assert.Equal(2, workers.Length);
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
