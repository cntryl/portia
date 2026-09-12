using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    IOptions<QueueRunnerOptions>? queueOptions = null,
    ILogger<FitzApplicationWorkers>? logger = null,
    ILogger<FitzRequestQueueConsumer>? queueLogger = null,
    ILogger<FitzNoticeRequestConsumer>? noticeLogger = null,
    ILogger<FitzScheduledRequestConsumer>? scheduleLogger = null,
    ILogger<QueueRunner>? runnerLogger = null,
    ILogger<RequestNotificationRunner>? notificationLogger = null)
    : BackgroundService, IHostedLifecycleService, IAsyncDisposable
{
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    readonly ILogger<FitzApplicationWorkers>? _logger = logger;
    readonly QueueRunnerOptions _queueOptions = queueOptions?.Value ?? new QueueRunnerOptions();
    readonly IReadOnlyList<FitzWorkerDefinition> _workers = configuration.Workers;
    readonly WorkloadRegistration[] _workloads = [.. workloads];
    IAsyncDisposable? _rpc;

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        Dispose();
        GC.SuppressFinalize(this);
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (_workers.OfType<FitzQueueWorkerDefinition>().Any() && _queueOptions.TerminalAttempt is > 0)
        {
            throw new InvalidOperationException(
                "QueueRunnerOptions.TerminalAttempt cannot be positive for a Fitz queue worker because Fitz 1.0 "
                + "does not report queue attempts. Remove the threshold or use a transport with a durable attempt count.");
        }

        var required = _workers.SelectMany(worker => worker.Requirements).ToHashSet();
        foreach (var registration in _workloads)
        {
            _ = required.Add(typeof(IEventStore));
            if (registration.Scope == WorkloadScope.PerTenant)
            {
                _ = required.Add(typeof(ITenantDirectory));
            }
        }

        var missing = required.Where(type => !available.IsService(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        return missing.Length == 0
            ? Task.CompletedTask
            : throw new InvalidOperationException(
                $"Portia worker setup requires: {string.Join(", ", missing.Select(type => type.FullName))}. Register them in the shared application setup.");
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseRpcAsync().ConfigureAwait(false);
        }
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Each definition builds its own runner, so a worker kind added later is hosted here
        // without an arm to add — and cannot silently fall into another kind's branch.
        var host = new FitzWorkerHost(connection.Client, scopes, serializer, _clock, queueLogger, noticeLogger,
            scheduleLogger, runnerLogger, notificationLogger);
        var tasks = new List<Task>();
        foreach (var worker in _workers)
        {
            if (worker.CreateRunner(host) is { } run)
            {
                tasks.Add(RetryAsync(worker.GetType().Name, run, TimeSpan.FromSeconds(1), _clock, _logger,
                    stoppingToken));
            }
        }

        return Task.WhenAll(tasks);
    }

    internal static async Task RetryAsync(string runnerName, Func<CancellationToken, Task> run, TimeSpan interval,
        TimeProvider clock, ILogger<FitzApplicationWorkers>? logger, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await run(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is TerminalHandlerFailureException or TerminalHandlerMissingException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PortiaTelemetry.RecordRunnerFault(runnerName, RunnerFaultStage.Execution, ex, logger);
            }

            PortiaTelemetry.RecordWorkerRestart(runnerName, "execution");
            try
            {
                await Task.Delay(interval, clock, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    async ValueTask ReleaseRpcAsync()
    {
        var rpc = Interlocked.Exchange(ref _rpc, null);
        if (rpc is not null)
        {
            await rpc.DisposeAsync().ConfigureAwait(false);
        }
    }
}
