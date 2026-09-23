using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

sealed class PortiaWorkloadService(
    IServiceScopeFactory scopes,
    IEnumerable<WorkloadRegistration> registrations,
    IServiceProviderIsService available,
    ITenantDirectory? tenantDirectory = null,
    IWorkloadCoordinator? workloadCoordinator = null,
    IDomainEventNotifier? notifier = null,
    TimeProvider? timeProvider = null,
    ILogger<PortiaWorkloadService>? logger = null,
    ILogger<MultiTenantRunner>? tenantLogger = null,
    PortiaStartupValidationRegistry? validations = null) : BackgroundService, IHostedLifecycleService
{
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    readonly ILogger<PortiaWorkloadService>? _logger = logger;
    readonly WorkloadRegistration[] _registrations = [.. registrations];

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (_registrations.Length == 0)
        {
            return;
        }

        Require(typeof(IDomainEventReader));
        if (_registrations.Any(item => item.Scope == WorkloadScope.PerTenant))
        {
            Require(typeof(ITenantDirectory));
        }

        if (workloadCoordinator is null)
        {
            throw new InvalidOperationException(
                $"Portia workers require an '{nameof(IWorkloadCoordinator)}'. Register a distributed coordinator such as AddFitz(...), " +
                $"or call {nameof(PortiaBuilder.UseSingleProcessWorkloads)}() when exactly one worker replica runs.");
        }

        foreach (var registration in _registrations)
        {
            await using var scope = scopes.CreateAsyncScope();
            var validationIdentity = registration.Scope == WorkloadScope.PerTenant
                ? new WorkloadIdentity(registration.Name, new TenantId("portia-startup-validation"))
                : new WorkloadIdentity(registration.Name);
            scope.ServiceProvider.GetRequiredService<WorkloadContext>()
                .Initialize(validationIdentity, registration.Name);
            var pattern = registration.Descriptor.Pattern(scope.ServiceProvider);
            var valid = registration.Scope == WorkloadScope.PerTenant
                ? pattern.IsTenantTemplate
                : !pattern.IsTenantTemplate;
            if (!valid)
            {
                var expected = registration.Scope == WorkloadScope.PerTenant
                    ? "EventStreamPattern.ForTenant(...)"
                    : "EventStreamPattern.ForPattern(...)";
                throw new InvalidOperationException(
                    $"Workload '{registration.Name}' uses scope '{registration.Scope}' but pattern '{pattern}'. " +
                    $"Use {expected} for this scope.");
            }

            registration.Descriptor.Validate(scope.ServiceProvider, registration.Name);
        }

        return;

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
        if (validations is not null)
            await validations.WaitForEndpointValidationsAsync(stoppingToken).ConfigureAwait(false);
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
            return new MultiTenantRunner(directory, logger: tenantLogger, timeProvider: _clock).RunAsync(
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
            await workloadCoordinator!.RunAsync(
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

    async Task RunAsync(WorkloadRegistration registration, WorkloadIdentity identity, CancellationToken ct)
    {
        var telemetryScope = identity.Tenant is null ? "global" : "tenant";
        PortiaTelemetry.RecordWorkload(registration.Name, telemetryScope, true, _logger);
        var consecutiveFailures = 0;
        IDomainEventSubscription? subscription = null;
        EventStreamPattern? subscribedPattern = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var failed = false;
                WorkloadFailureException? terminalFailure = null;
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var provider = scope.ServiceProvider;
                    provider.GetRequiredService<WorkloadContext>().Initialize(identity, registration.Name);
                    registration.Descriptor.Bind(provider, identity, registration.Name);
                    var passPattern = registration.Descriptor.Pattern(provider);
                    if (subscription is null)
                    {
                        subscription = await SubscribeAsync(passPattern, ct).ConfigureAwait(false);
                        subscribedPattern = passPattern;
                    }
                    else if (passPattern != subscribedPattern)
                    {
                        throw new InvalidOperationException(
                            $"Workload '{identity}' changed its event-stream pattern from '{subscribedPattern}' to '{passPattern}' across dependency-injection scopes.");
                    }

                    var pass = await registration.Descriptor.RunPass(provider, registration.Processing, ct)
                        .ConfigureAwait(false);
                    consecutiveFailures = 0;
                    if (pass.ContinueImmediately)
                    {
                        continue;
                    }
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

                if (terminalFailure is not null)
                {
                    throw terminalFailure;
                }

                if (failed)
                {
                    try
                    {
                        await Task.Delay(GetFailureDelay(registration, consecutiveFailures), _clock, ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    continue;
                }

                if (subscription is null)
                {
                    try
                    {
                        await Task.Delay(registration.PollInterval, _clock, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    continue;
                }

                // The backstop is the same application-configured poll interval the delays above
                // use, so it is scheduled on the same clock. CreateLinkedTokenSource takes no
                // TimeProvider, so the deadline is its own source and the linked one only combines
                // it with the stopping token.
                using var deadline = new CancellationTokenSource(registration.PollInterval, _clock);
                using var backstop = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
                try
                {
                    await subscription.WaitAsync(backstop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested &&
                                                         deadline.IsCancellationRequested)
                {
                    // A notification is only a wakeup. Periodically re-read durable state in
                    // case a reconnect or bounded subscription buffer lost the signal.
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    PortiaTelemetry.RecordRunnerFault(nameof(PortiaWorkloadService), RunnerFaultStage.Notification, ex,
                        _logger);
                    await subscription.DisposeAsync().ConfigureAwait(false);
                    subscription = null;
                    subscribedPattern = null;
                    try
                    {
                        await Task.Delay(registration.PollInterval, _clock, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            if (subscription is not null)
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
            }

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
}
