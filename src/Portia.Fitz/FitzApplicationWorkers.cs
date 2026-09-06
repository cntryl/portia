using Cntryl.Fitz.Abstractions.Domains.Lease;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

sealed class FitzApplicationWorkers(IServiceProvider services, PortiaFitzBuilder configuration)
    : BackgroundService, IHostedLifecycleService, IAsyncDisposable
{
    readonly IServiceScopeFactory _scopes = services.GetRequiredService<IServiceScopeFactory>();
    readonly TimeProvider _clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
    readonly ILogger<FitzApplicationWorkers>? _logger = services.GetService<ILogger<FitzApplicationWorkers>>();
    IAsyncDisposable? _rpc;

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        var available = services.GetRequiredService<IServiceProviderIsService>();
        if (configuration.Workers.Count > 0)
        {
            Require(available, typeof(IRequestBus));
            Require(available, typeof(IRequestActorValidator));
            Require(available, typeof(IRequestDeserializer));
            if (configuration.Workers.Any(worker => worker.Kind == "rpc"))
                Require(available, typeof(IRequestOutcomeSerializer));
        }
        if (configuration.Components.Count > 0)
        {
            if (configuration.Fleet is null)
                throw new InvalidOperationException("Fitz component workers require UseFleet with a dedicated membership selector.");
            configuration.Fleet.Validate([.. configuration.Components.Select(component => component.LeaseRoute)]);
            Require(available, typeof(IDomainEventReader));
            var projectors = services.GetServices<ProjectorRegistration>().ToArray();
            var reactors = services.GetServices<ReactorRegistration>().ToArray();
            var existingHosts = services.GetServices<PortiaHostedComponentRegistration>().ToArray();
            foreach (var component in configuration.Components)
            {
                if (existingHosts.Any(host => host.ComponentType == component.Type))
                    throw new InvalidOperationException($"Component '{component.Type}' is already hosted through the low-level Portia hosting API.");
                var exists = component.Projector ? projectors.Any(item => item.ProjectorType == component.Type)
                    : reactors.Any(item => item.ReactorType == component.Type);
                if (!exists)
                    throw new InvalidOperationException($"Worker component '{component.Type}' is missing its generated module registration.");
                Require(available, component.Type);
                if (component.Projector)
                    Require(available, projectors.Single(item => item.ProjectorType == component.Type).ProjectionTargetType);
                if (!component.Projector)
                    Require(available, typeof(IProjectionCheckpointStore));
            }
        }
        return Task.CompletedTask;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Also validate for callers that start the hosted service directly.
        await StartingAsync(cancellationToken).ConfigureAwait(false);
        var connection = services.GetRequiredService<FitzApplicationConnection>();
        await connection.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (configuration.Workers.Any(worker => worker.Kind == "rpc"))
            {
                _rpc = await new FitzRpcRequestServer(connection.Client.Rpc, _scopes)
                    .RegisterModulesAsync(cancellationToken).ConfigureAwait(false);
            }

            await base.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await ReleaseRpcAsync().ConfigureAwait(false);
            throw;
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = services.GetRequiredService<FitzApplicationConnection>().Client;
        var tasks = new List<Task>();
        foreach (var worker in configuration.Workers)
        {
            if (worker.Kind == "rpc")
                continue;
            var serializer = services.GetRequiredService<IRequestDeserializer>();
            if (worker.Kind == "queue")
            {
                var runner = new QueueRunner(new FitzRequestQueueConsumer(client.Queue, serializer, worker.Route,
                    timeProvider: _clock, logger: services.GetService<ILogger<FitzRequestQueueConsumer>>()),
                    _scopes, services.GetService<ILogger<QueueRunner>>());
                tasks.Add(RetryAsync(worker.Route, runner.RunAsync, TimeSpan.FromSeconds(1), stoppingToken));
            }
            else
            {
                IRequestNotificationConsumer consumer = worker.Kind == "notice"
                    ? new FitzNoticeRequestConsumer(client.Notice, serializer, worker.Route)
                    : new FitzScheduledRequestConsumer(client.Schedule, serializer, worker.Route);
                var runner = new RequestNotificationRunner(consumer, _scopes, services.GetService<ILogger<RequestNotificationRunner>>());
                tasks.Add(RetryAsync(worker.Route, runner.RunAsync, TimeSpan.FromSeconds(1), stoppingToken));
            }
        }
        if (configuration.Components.Count > 0)
        {
            var fleet = new FleetPartitionRunner(
                new FitzPartitionLeaseCompetitor(client.Lease), new FitzFleetMembership(client.Lease),
                services.GetService<ILogger<FleetPartitionRunner>>(), _clock);
            tasks.Add(fleet.RunAsync([.. configuration.Components.Select(component => component.LeaseRoute)],
                RunComponentAsync, configuration.Fleet!, stoppingToken));
        }
        return Task.WhenAll(tasks);
    }

    Task RunComponentAsync(string route, LeaseAuthority authority, CancellationToken ct)
    {
        var component = configuration.Components.Single(item => item.LeaseRoute == route);
        return RetryAsync(route, async token =>
        {
            await using var scope = _scopes.CreateAsyncScope();
            var provider = scope.ServiceProvider;
            var lease = provider.GetRequiredService<WorkerLeaseContext>();
            lease.Route = route;
            lease.Authority = authority;
            if (component.Projector)
            {
                var registration = provider.GetServices<ProjectorRegistration>().Single(item => item.ProjectorType == component.Type);
                await registration.RunPass(provider, component.Options, token).ConfigureAwait(false);
            }
            else
            {
                var registration = provider.GetServices<ReactorRegistration>().Single(item => item.ReactorType == component.Type);
                var reactor = registration.Resolve(provider);
                var checkpoints = provider.GetRequiredService<IProjectionCheckpointStore>();
                var checkpoint = await checkpoints.LoadAsync(new CheckpointIdentity(reactor.Name, reactor.Pattern), token).ConfigureAwait(false);
                _ = await provider.GetRequiredService<ReactorRunner>().RunAsync(reactor, checkpoint,
                    checkpoints, component.Options.MaxBatchSize, token).ConfigureAwait(false);
            }
        }, component.Interval, ct);
    }

    async Task RetryAsync(string name, Func<CancellationToken, Task> run, TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await run(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { PortiaTelemetry.RecordRunnerFault(name, "worker pass failed", ex, _logger); }
            try { await Task.Delay(interval, _clock, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await base.StopAsync(cancellationToken).ConfigureAwait(false); }
        finally { await ReleaseRpcAsync().ConfigureAwait(false); }
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    async ValueTask ReleaseRpcAsync()
    {
        var rpc = Interlocked.Exchange(ref _rpc, null);
        if (rpc is not null)
            await rpc.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        Dispose();
        GC.SuppressFinalize(this);
    }

    static void Require(IServiceProviderIsService services, Type type)
    {
        if (!services.IsService(type))
            throw new InvalidOperationException($"Portia worker setup requires '{type}'. Register it in the shared application setup.");
    }
}
