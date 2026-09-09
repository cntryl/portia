using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

sealed class FitzApplicationWorkers(
    FitzApplicationConnection connection,
    PortiaFitzBuilder configuration,
    IServiceScopeFactory scopes,
    IServiceProviderIsService available,
    IEnumerable<WorkloadRegistration> workloads,
    RequestTransportCatalog catalog,
    IRequestDeserializer serializer,
    TimeProvider? timeProvider = null,
    ILogger<FitzApplicationWorkers>? logger = null,
    ILogger<FitzRequestQueueConsumer>? queueLogger = null,
    ILogger<QueueRunner>? runnerLogger = null,
    ILogger<RequestNotificationRunner>? notificationLogger = null)
    : BackgroundService, IHostedLifecycleService, IAsyncDisposable
{
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    readonly ILogger<FitzApplicationWorkers>? _logger = logger;
    readonly IReadOnlyList<FitzWorkerDefinition> _workers = configuration.Workers;
    readonly WorkloadRegistration[] _workloads = [.. workloads];
    IAsyncDisposable? _rpc;

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        var required = _workers.SelectMany(worker => worker.Requirements).ToHashSet();
        foreach (var registration in _workloads)
        {
            _ = required.Add(typeof(IEventStore));
            if (registration.Scope == WorkloadScope.PerTenant)
                _ = required.Add(typeof(ITenantDirectory));
        }
        var missing = required.Where(type => !available.IsService(type)).OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        return missing.Length == 0
            ? Task.CompletedTask
            : throw new InvalidOperationException($"Portia worker setup requires: {string.Join(", ", missing.Select(type => type.FullName))}. Register them in the shared application setup.");
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Also validate for callers that start the hosted service directly.
        await StartingAsync(cancellationToken).ConfigureAwait(false);
        await connection.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_workers.OfType<FitzRpcWorkerDefinition>().Any())
            {
                _rpc = await new FitzRpcRequestServer(connection.Client.Rpc, scopes, catalog)
                    .RegisterRequestsAsync(cancellationToken).ConfigureAwait(false);
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
        var client = connection.Client;
        var tasks = new List<Task>();
        foreach (var worker in _workers)
        {
            if (worker is FitzRpcWorkerDefinition) continue;
            if (worker is FitzQueueWorkerDefinition)
            {
                var runner = new QueueRunner(new FitzRequestQueueConsumer(client.Queue, serializer, worker.Route,
                    timeProvider: _clock, logger: queueLogger),
                    new DependencyInjectionQueueDeliveryScopeFactory(scopes), runnerLogger);
                tasks.Add(RetryAsync(worker.Route, runner.RunAsync, TimeSpan.FromSeconds(1), stoppingToken));
            }
            else
            {
                IRequestNotificationConsumer consumer = worker is FitzNoticeWorkerDefinition
                    ? new FitzNoticeRequestConsumer(client.Notice, serializer, worker.Route)
                    : new FitzScheduledRequestConsumer(client.Schedule, serializer, worker.Route);
                var runner = new RequestNotificationRunner(consumer,
                    new DependencyInjectionRequestDeliveryScopeFactory(scopes), notificationLogger);
                tasks.Add(RetryAsync(worker.Route, runner.RunAsync, TimeSpan.FromSeconds(1), stoppingToken));
            }
        }
        return Task.WhenAll(tasks);
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

}
