using Cntryl.Fitz;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

public sealed class ApplicationFleetTests
{
    [Fact]
    public async Task WorkerReplicasHandOffLeasedComponentWithoutConcurrentExecution()
    {
        await using var firstClient = await ConsumerBroker.ConnectAsync();
        await using var secondClient = await ConsumerBroker.ConnectAsync();
        var store = new InMemoryEventStore();
        var id = Uuid.CreateVersion4();
        await store.AppendAsync(new EventStreamAddress("application-fleet", "events", id.ToString()), 0,
            [DomainEventSeed.Attach(new Deposited(1), id, 1)]);
        var probe = new Probe();
        var realm = "app-" + id;
        using var first = Build(firstClient, "a", realm, store, probe);
        using var second = Build(secondClient, "b", realm, store, probe);
        await first.StartAsync();
        try
        {
            await Until(() => probe.Owner == "a");
            await second.StartAsync();
            try
            {
                await first.StopAsync();
                await Until(() => probe.Owner == "b");
                Assert.Equal(1, probe.MaximumActive);
                Assert.True(probe.LastFence > 0);
            }
            finally { await second.StopAsync(); }
        }
        finally { await first.StopAsync(); }
        Assert.Equal(0, probe.Active);
        Assert.Equal(probe.Created, probe.Disposed);
    }

    static IHost Build(Client client, string worker, string realm, InMemoryEventStore store, Probe probe)
    {
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Services.AddSingleton<IDomainEventReader>(store);
        _ = builder.Services.AddSingleton<IProjectionCheckpointStore, InMemoryProjectionCheckpointStore>();
        _ = builder.Services.AddScoped(provider => new ProbeReactor(worker, provider.GetRequiredService<WorkloadContext>(), probe));
        _ = builder.Services.AddSingleton(new ReactorRegistration(typeof(ProbeReactor), provider => provider.GetRequiredService<ProbeReactor>()));
        _ = builder.Services.AddPortia(portia =>
        {
            _ = portia.AddReactor<ProbeReactor>(o => { o.Global(); o.PollInterval = TimeSpan.FromMilliseconds(10); });
            _ = portia.UseFitzClient(client, fitz => _ = fitz.UseFleet(new FleetRunOptions
            {
                MembershipSelector = $"lease://{realm}/members/*",
                WorkerId = worker,
                LeaseTtl = TimeSpan.FromSeconds(2),
                ReconciliationInterval = TimeSpan.FromMilliseconds(50),
            }));
        }).AddWorker();
        return builder.Build();
    }

    static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    sealed class Probe
    {
        public string? Owner;
        public int Active;
        public int MaximumActive;
        public int Created;
        public int Disposed;
        public ulong LastFence;
    }

    sealed class ProbeReactor : BaseReactor, IDisposable
    {
        readonly string _worker;
        readonly WorkloadContext _lease;
        readonly Probe _probe;

        public ProbeReactor(string worker, WorkloadContext lease, Probe probe)
            : base(new InMemoryProjectionCheckpointStore(), EventStreamPattern.ForPattern("application-fleet", "events"), "probe")
        {
            _worker = worker;
            _lease = lease;
            _probe = probe;
            _ = Interlocked.Increment(ref probe.Created);
        }

        protected override async ValueTask ReactToEventAsync(DomainEventRecord record, IExecutionContext context, CancellationToken ct)
        {
            lock (_probe)
            {
                _probe.Active++;
                _probe.MaximumActive = Math.Max(_probe.MaximumActive, _probe.Active);
                _probe.LastFence = _lease.FencingToken;
                _probe.Owner = _worker;
            }
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally
            {
                lock (_probe)
                {
                    _probe.Owner = null;
                    _probe.Active--;
                }
            }
        }

        public void Dispose() => Interlocked.Increment(ref _probe.Disposed);
    }
}
