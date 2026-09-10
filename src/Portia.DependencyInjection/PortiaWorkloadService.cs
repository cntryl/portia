using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

sealed partial class PortiaWorkloadService(
    IServiceScopeFactory scopes,
    IEnumerable<WorkloadRegistration> registrations,
    IServiceProviderIsService available,
    ITenantDirectory? tenantDirectory = null,
    IWorkloadCoordinator? workloadCoordinator = null,
    IDomainEventNotifier? notifier = null,
    TimeProvider? timeProvider = null,
    ILogger<PortiaWorkloadService>? logger = null,
    ILogger<MultiTenantRunner>? tenantLogger = null) : BackgroundService, IHostedLifecycleService
{
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    readonly ILogger<PortiaWorkloadService>? _logger = logger;
    readonly WorkloadRegistration[] _registrations = [.. registrations];

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (_registrations.Length == 0)
        {
            return Task.CompletedTask;
        }

        Require(typeof(IDomainEventReader));
        if (_registrations.Any(item => item.Scope == WorkloadScope.PerTenant))
        {
            Require(typeof(ITenantDirectory));
        }

        return Task.CompletedTask;

        void Require(Type type)
        {
            if (!available.IsService(type))
            {
                throw new InvalidOperationException(
                    $"Portia workers require '{type.Name}'. Register its application or infrastructure implementation.");
            }
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await StartingAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_registrations.Length == 0)
        {
            return;
        }

        var active = new ConcurrentDictionary<WorkloadIdentity, WorkloadRegistration>();
        foreach (var registration in _registrations.Where(item => item.Scope == WorkloadScope.Global))
            active[new WorkloadIdentity(registration.Name)] = registration;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var perTenant = _registrations.Where(item => item.Scope == WorkloadScope.PerTenant).ToArray();
        var tenants = perTenant.Length == 0
            ? Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token)
            : RunTenantManager();
        var coordinator = CoordinateAsync();

        Task RunTenantManager()
        {
            var directory = tenantDirectory ?? throw new InvalidOperationException(
                $"Portia workers require '{nameof(ITenantDirectory)}'. Register its application or infrastructure implementation.");
            return new MultiTenantRunner(directory, tenantLogger, _clock).RunAsync(
                async (tenant, ct) =>
                {
                    var identities = perTenant.Select(item => new WorkloadIdentity(item.Name, tenant)).ToArray();
                    for (var i = 0; i < identities.Length; i++)
                        active[identities[i]] = perTenant[i];
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        foreach (var identity in identities)
                            _ = active.TryRemove(identity, out _);
                    }
                }, (_, _) => Task.CompletedTask, lifetime.Token);
        }

        async Task CoordinateAsync()
        {
            await ResolveCoordinator().RunAsync(
                () => [.. active.Keys],
                (identity, ct) => active.TryGetValue(identity, out var registration)
                    ? RunAsync(registration, identity, ct)
                    : Task.CompletedTask, lifetime.Token).ConfigureAwait(false);
        }

        try
        {
            _ = await Task.WhenAny(tenants, coordinator).ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(tenants, coordinator).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }

        if (!stoppingToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The workload coordinator or tenant directory stopped unexpectedly.");
        }
    }

    /// <summary>
    ///     Uses the application's coordinator when infrastructure supplies one, and otherwise owns
    ///     every workload in this process. Resolving the fallback here rather than registering it in
    ///     <c>AddWorkers()</c> keeps it immune to setup order: infrastructure registered after
    ///     <c>AddWorkers()</c> is still the coordinator that runs.
    /// </summary>
    IWorkloadCoordinator ResolveCoordinator()
    {
        if (workloadCoordinator is not null)
        {
            return workloadCoordinator;
        }

        if (_logger is not null)
        {
            LogSingleProcessCoordinator(_logger);
        }

        // Reconcile cadence is infrastructure timing, not application time, so it deliberately
        // does not follow a registered TimeProvider — a test that controls the workload poll
        // interval with a fake clock would otherwise also be driving ownership reconciliation.
        return new SingleProcessWorkloadCoordinator();
    }

    async Task RunAsync(WorkloadRegistration registration, WorkloadIdentity identity, CancellationToken ct)
    {
        var telemetryScope = identity.Tenant is null ? "global" : "tenant";
        PortiaTelemetry.RecordWorkload(registration.Name, telemetryScope, true, _logger);
        var consecutiveFailures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                IDomainEventSubscription? subscription = null;
                var failed = false;
                WorkloadFailureException? terminalFailure = null;
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var provider = scope.ServiceProvider;
                    provider.GetRequiredService<WorkloadContext>().Initialize(identity, registration.ExplicitName);
                    registration.Descriptor.Bind(provider, identity, registration.ExplicitName);
                    subscription = await SubscribeAsync(registration.Descriptor.Pattern(provider), ct)
                        .ConfigureAwait(false);
                    await registration.Descriptor.RunPass(provider, registration.Processing, ct).ConfigureAwait(false);
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    failed = true;
                    consecutiveFailures++;
                    PortiaTelemetry.RecordRunnerFault(nameof(PortiaWorkloadService), RunnerFaultStage.Workload, ex,
                        _logger);
                    if (consecutiveFailures >= registration.FailureAttemptLimit)
                    {
                        terminalFailure = new WorkloadFailureException(identity, consecutiveFailures, ex);
                    }
                }

                try
                {
                    if (!failed && subscription is not null)
                    {
                        using var backstop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        backstop.CancelAfter(registration.PollInterval);
                        try
                        {
                            await subscription.WaitAsync(backstop.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested &&
                                                                 backstop.IsCancellationRequested)
                        {
                            // A notification is only a wakeup. Periodically re-read durable state in
                            // case a reconnect or bounded subscription buffer lost the signal.
                        }
                    }
                    else
                    {
                        if (terminalFailure == null)
                        {
                            var delay = failed
                                ? GetFailureDelay(registration, consecutiveFailures)
                                : registration.PollInterval;
                            await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    PortiaTelemetry.RecordRunnerFault(nameof(PortiaWorkloadService), RunnerFaultStage.Notification, ex,
                        _logger);
                    try
                    {
                        await Task.Delay(registration.PollInterval, _clock, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                }
                finally
                {
                    if (subscription is not null)
                    {
                        await subscription.DisposeAsync().ConfigureAwait(false);
                    }
                }

                if (terminalFailure is not null)
                {
                    throw terminalFailure;
                }
            }
        }
        finally
        {
            PortiaTelemetry.RecordWorkload(registration.Name, telemetryScope, false, _logger);
        }

        ValueTask<IDomainEventSubscription?> SubscribeAsync(EventStreamPattern pattern, CancellationToken token)
        {
            return notifier is not null
                ? SubscribeCoreAsync(notifier, pattern, token)
                : ValueTask.FromResult<IDomainEventSubscription?>(null);
        }

        static async ValueTask<IDomainEventSubscription?> SubscribeCoreAsync(
            IDomainEventNotifier notifier,
            EventStreamPattern pattern,
            CancellationToken token)
        {
            return await notifier.SubscribeAsync(pattern, token).ConfigureAwait(false);
        }
    }

    static TimeSpan GetFailureDelay(WorkloadRegistration registration, int consecutiveFailures)
    {
        var maximumTicks = registration.MaximumFailureDelay.Ticks;
        var initialTicks = Math.Min(registration.PollInterval.Ticks, maximumTicks);
        var exponent = Math.Min(consecutiveFailures - 1, 30);
        var ticks = initialTicks > maximumTicks >> exponent
            ? maximumTicks
            : initialTicks << exponent;
        return TimeSpan.FromTicks(Math.Min(ticks, maximumTicks));
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message =
            "No IWorkloadCoordinator is registered; owning every workload in this process. That is correct for a single worker replica only \u2014 register a distributed coordinator before scaling workers out.")]
    static partial void LogSingleProcessCoordinator(ILogger logger);
}
