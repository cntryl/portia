using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

public sealed class MixedWorkloadTests
{
    [Fact]
    public async Task MixedScopesIsolateProgressAndReconcileTenantsWithoutRestartingGlobalWork()
    {
        var builder = Host.CreateApplicationBuilder();
        foreach (var descriptor in ConsumerHost.CreateServices())
            builder.Services.Add(descriptor);
        _ = builder.Services.AddSingleton<ObservedScopes>();
        _ = builder.Services.AddScoped<IConsumerScope, ScopeProbe>();
        var coordinator = new Coordinator();
        var tenants = new Directory();
        _ = builder.Services.AddSingleton<IWorkloadCoordinator>(coordinator);
        _ = builder.Services.AddSingleton<ITenantDirectory>(tenants);
        _ = builder.Services.AddPortia(p =>
        {
            _ = p.AddProjector<FirstProjector>(o => { o.PerTenant(); o.Name = "accounts"; o.PollInterval = TimeSpan.FromMilliseconds(10); });
            _ = p.AddProjector<SecondProjector>(o => { o.Global(); o.Name = "summary"; o.PollInterval = TimeSpan.FromMilliseconds(10); });
            _ = p.AddReactor<FirstReactor>(o => { o.PerTenant(); o.Name = "reaction"; o.PollInterval = TimeSpan.FromMilliseconds(10); });
        }).AddWorker();
        using var host = builder.Build();
        var store = host.Services.GetRequiredService<IEventStore>();
        var ids = new[] { Uuid.CreateVersion4(), Uuid.CreateVersion4(), Uuid.CreateVersion4() };
        var realms = new[] { "alpha", "beta", "consumer" };
        for (var i = 0; i < realms.Length; i++)
        {
            await store.AppendAsync(new EventStreamAddress(realms[i], "accounts", ids[i].ToString()), 0,
                [DomainEventSeed.Attach(new Deposited(i + 1), ids[i], 1)]);
        }

        await host.StartAsync();
        try
        {
            var effects = host.Services.GetRequiredService<ConsumerHost.Effects>();
            await effects.WaitForAsync("accounts", aggregateId: ids[0]);
            await effects.WaitForAsync("reaction", aggregateId: ids[0]);
            await effects.WaitForAsync("summary", aggregateId: ids[2]);
            tenants.Change(TenantLifecycleChangeKind.Added, "beta");
            await effects.WaitForAsync("accounts", aggregateId: ids[1]);
            await effects.WaitForAsync("reaction", aggregateId: ids[1]);
            var global = new WorkloadIdentity("summary");
            Assert.Equal(1, coordinator.Starts[global]);
            Assert.DoesNotContain(effects.Items, item => item.Component == "summary" && item.AggregateId != ids[2]);
            Assert.DoesNotContain(effects.Items, item => item.Component == "accounts" && item.AggregateId == ids[2]);
            var storage = host.Services.GetRequiredService<ConsumerHost.ProjectionStorage>();
            Assert.Equal(3, storage.Checkpoints.Count);
            tenants.Change(TenantLifecycleChangeKind.Removed, "alpha");
            await Until(() => !coordinator.Active.ContainsKey(new WorkloadIdentity("accounts", new TenantId("alpha")))
                && !coordinator.Active.ContainsKey(new WorkloadIdentity("reaction", new TenantId("alpha"))));
            await store.AppendAsync(new EventStreamAddress("alpha", "accounts", ids[0].ToString()), 1,
                [DomainEventSeed.Attach(new Deposited(8), ids[0], 2)]);
            tenants.Change(TenantLifecycleChangeKind.Added, "alpha");
            await effects.WaitForAsync("accounts", 2, ids[0]);
            await effects.WaitForAsync("reaction", 2, ids[0]);
            Assert.Equal([1, 8], effects.Items.Where(item => item.Component == "accounts" && item.AggregateId == ids[0]).Select(item => item.Amount));
            Assert.Equal([1, 8], effects.Items.Where(item => item.Component == "reaction" && item.AggregateId == ids[0]).Select(item => item.Amount));
            Assert.Equal(1, coordinator.Starts[global]);
        }
        finally { await host.StopAsync(); }
        var scopes = host.Services.GetRequiredService<ObservedScopes>();
        Assert.Contains(scopes.Items, item => item.Identity.Name == "summary" && item.Identity.Tenant is null);
        Assert.Contains(scopes.Items, item => item.Identity.Name == "accounts" && item.Identity.Tenant == new TenantId("alpha"));
        Assert.Contains(scopes.Items, item => item.Identity.Name == "reaction" && item.Identity.Tenant == new TenantId("beta"));
        Assert.All(scopes.Items, item => Assert.Equal(42UL, item.FencingToken));
        Assert.Empty(coordinator.Active);
        Assert.All(host.Services.GetRequiredService<ConsumerHost.Effects>().Scopes.Values, Assert.True);
    }

    static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    sealed class ObservedScopes
    {
        public ConcurrentBag<WorkloadContext> Items { get; } = [];
    }

    sealed class ScopeProbe : IConsumerScope, IDisposable
    {
        readonly ConsumerHost.Effects _effects;
        public ScopeProbe(WorkloadContext context, ObservedScopes observed, ConsumerHost.Effects effects)
        {
            _ = context.Identity;
            observed.Items.Add(context);
            _effects = effects;
            effects.Scopes[Id] = false;
        }
        public Guid Id { get; } = Guid.NewGuid();
        public void Dispose() => _effects.Scopes[Id] = true;
    }

    sealed class Directory : ITenantDirectory
    {
        readonly Channel<TenantLifecycleChange> _changes = Channel.CreateUnbounded<TenantLifecycleChange>();
        public void Change(TenantLifecycleChangeKind kind, string tenant) => Assert.True(_changes.Writer.TryWrite(new(kind, new TenantId(tenant))));
        public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            yield return new TenantId("alpha");
            await Task.CompletedTask;
        }
        public IAsyncEnumerable<TenantLifecycleChange> WatchAsync(CancellationToken ct = default) => _changes.Reader.ReadAllAsync(ct);
    }

    sealed class Coordinator : IWorkloadCoordinator
    {
        public ConcurrentDictionary<WorkloadIdentity, bool> Active { get; } = new();
        public ConcurrentDictionary<WorkloadIdentity, int> Starts { get; } = new();
        public async Task RunAsync(Func<IReadOnlyCollection<WorkloadIdentity>> workloads,
            Func<WorkloadIdentity, ulong, CancellationToken, Task> run, CancellationToken ct = default)
        {
            var runs = new Dictionary<WorkloadIdentity, (CancellationTokenSource Cancellation, Task Task)>();
            async Task Stop(WorkloadIdentity id)
            {
                var item = runs[id];
                await item.Cancellation.CancelAsync();
                try { await item.Task; }
                catch (OperationCanceledException) when (item.Cancellation.IsCancellationRequested) { }
                item.Cancellation.Dispose();
                _ = runs.Remove(id);
                _ = Active.TryRemove(id, out _);
            }
            try
            {
                while (true)
                {
                    var snapshot = workloads();
                    foreach (var id in runs.Keys.Except(snapshot).ToArray())
                        await Stop(id);
                    foreach (var id in snapshot.Except(runs.Keys).ToArray())
                    {
                        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        _ = Starts.AddOrUpdate(id, 1, (_, n) => n + 1);
                        Active[id] = true;
                        runs.Add(id, (cancellation, run(id, 42, cancellation.Token)));
                    }
                    await Task.Delay(10, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally
            {
                foreach (var id in runs.Keys.ToArray())
                    await Stop(id);
            }
        }
    }
}
