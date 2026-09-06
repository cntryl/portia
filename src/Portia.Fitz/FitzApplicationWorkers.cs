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

    static void Require(IServiceProviderIsService services, Type type)
    {
        if (!services.IsService(type))
            throw new InvalidOperationException($"Portia worker setup requires '{type}'. Register it in the shared application setup.");
    }
}
