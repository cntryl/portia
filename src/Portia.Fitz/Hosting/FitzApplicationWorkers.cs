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
    ILogger<FitzApplicationWorkers>? logger = null,
    ILogger<FitzRequestQueueConsumer>? queueLogger = null,
    ILogger<FitzNoticeRequestConsumer>? noticeLogger = null,
    ILogger<FitzScheduledRequestConsumer>? scheduleLogger = null,
    ILogger<QueueRunner>? runnerLogger = null,
    ILogger<RequestNotificationRunner>? notificationLogger = null,
    PortiaStartupValidationRegistry? validations = null)
    : BackgroundService, IHostedLifecycleService, IAsyncDisposable
{
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    readonly ILogger<FitzApplicationWorkers>? _logger = logger;
    readonly IReadOnlyList<FitzWorkerDefinition> _workers = configuration.Workers;
    readonly WorkloadRegistration[] _workloads = [.. workloads];
    bool _deferredStart;
    IAsyncDisposable? _rpc;

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        Dispose();
        GC.SuppressFinalize(this);
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (_workloads.Length != 0)
        {
            using var scope = scopes.CreateScope();
            var coordinator = scope.ServiceProvider.GetService<IWorkloadCoordinator>();
            if (coordinator is FitzWorkloadCoordinator && configuration.Fleet is null)
                throw new InvalidOperationException(
                    "Fitz:ApplicationName or an explicit UseFleet(...) is required when Fitz coordinates workloads.");
        }

        if (_workers.OfType<FitzQueueWorkerDefinition>().Any())
        {
            using var scope = scopes.CreateScope();
            var options = scope.ServiceProvider.GetService<IOptions<QueueRunnerOptions>>()?.Value
                          ?? new QueueRunnerOptions();
            options.Validate();
            if (options.TerminalAttempt is > 0)
                throw new InvalidOperationException(
                    "Fitz queue workers do not support QueueRunnerOptions.TerminalAttempt because Fitz does not expose durable attempt counts.");
        }

        var required = _workers.SelectMany(worker => worker.Requirements).ToHashSet();
        if (_workers.OfType<FitzScheduleWorkerDefinition>().Any())
            _ = required.Add(typeof(IScheduledRequestActorValidator));
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
        if (validations?.HasPendingEndpointValidations is true)
        {
            _deferredStart = true;
            return;
        }

        await StartWorkersAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        if (!_deferredStart)
            return;
        _deferredStart = false;
        await validations!.WaitForEndpointValidationsAsync(cancellationToken).ConfigureAwait(false);
        await StartWorkersAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    async Task StartWorkersAsync(CancellationToken cancellationToken)
    {
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
        // Each definition builds its own runner, so a worker kind added later is hosted here
        // without an arm to add — and cannot silently fall into another kind's branch.
        var host = new FitzWorkerHost(connection.Client, scopes, serializer, catalog, _clock,
            queueLogger, noticeLogger, scheduleLogger, runnerLogger, notificationLogger);
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
            catch (Exception ex) when (ex is TerminalHandlerFailureException or TerminalHandlerMissingException or
                                           QueueConfigurationException)
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
