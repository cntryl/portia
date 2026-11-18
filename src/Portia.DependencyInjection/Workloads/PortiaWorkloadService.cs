using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

sealed class PortiaWorkloadService(
    IServiceScopeFactory scopes,
    IEnumerable<WorkloadRegistration> registrations,
    IServiceProviderIsService available,
    IEnumerable<IWorkloadCoordinator> workloadCoordinators,
    ITenantDirectory? tenantDirectory = null,
    IDomainEventNotifier? notifier = null,
    TimeProvider? timeProvider = null,
    ILogger<PortiaWorkloadService>? logger = null,
    ILogger<MultiTenantRunner>? tenantLogger = null,
    PortiaStartupValidationRegistry? validations = null,
    IOperationalStore? operationalStore = null,
    OperationalOptions? operationalOptions = null,
    IApplicationTopology? topology = null,
    IProcessorStatusRpcRegistrar? statusRpc = null) : BackgroundService, IHostedLifecycleService
{
    static readonly Action<ILogger, string, string, int, string, TimeSpan, string, Exception?> LogFailureDecision =
        LoggerMessage.Define<string, string, int, string, TimeSpan, string>(LogLevel.Error, new EventId(2001),
            "Workload {Component} failed during {Stage} on attempt {Attempt}; decision {Decision}, retry delay {Delay}, failure kind {FailureKind}");

    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    readonly ILogger<PortiaWorkloadService>? _logger = logger;
    readonly WorkloadRegistration[] _registrations = [.. registrations];
    readonly Lock _startingGate = new();
    IWorkloadCoordinator? _coordinator;
    Task? _starting;

    // The host calls StartingAsync and then StartAsync, which also validates for callers that start the
    // service directly. Validation runs once; a failed or cancelled attempt may be retried.
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        lock (_startingGate)
        {
            if (_starting is null || _starting.IsFaulted || _starting.IsCanceled)
                _starting = ValidateStartupAsync(cancellationToken);
            return _starting;
        }
    }

    async Task ValidateStartupAsync(CancellationToken cancellationToken)
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

        IWorkloadCoordinator[] coordinators = [.. workloadCoordinators];
        if (coordinators.Length != 1)
        {
            throw new InvalidOperationException(
                $"Portia workers require exactly one '{nameof(IWorkloadCoordinator)}' but found {coordinators.Length}. " +
                "Register a distributed coordinator such as AddFitz(...), " +
                $"or call {nameof(PortiaBuilder.UseSingleProcessWorkloads)}() when exactly one worker replica runs.");
        }

        _coordinator = coordinators[0];

        if (operationalStore is not null)
        {
            if (operationalOptions is null || topology is null || !available.IsService(typeof(OperationalExecution)))
                throw new InvalidOperationException(
                    "Operational workers require the complete operations registration from their infrastructure builder.");
            operationalOptions.Validate();
            await using var operationalScope = scopes.CreateAsyncScope();
            var checkpoints = operationalScope.ServiceProvider.GetServices<IProjectionCheckpointStore>().ToArray();
            if (checkpoints.Length != 1 || !ReferenceEquals(checkpoints[0],
                    operationalScope.ServiceProvider.GetService<IOperationalReactionCheckpointStore>()))
                throw new InvalidOperationException(
                    "Operational workers require exactly one operational checkpoint registration.");
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
            await _coordinator!.RunAsync(
                () => [.. active.Keys],
                (identity, ct) => active.TryGetValue(identity, out var registration)
                    ? RunAsync(registration, identity, ct)
                    : Task.CompletedTask, lifetime.Token).ConfigureAwait(false);
        }

        _ = await Task.WhenAny(tenants, coordinator).ConfigureAwait(false);
        Exception? cancellationFailure = null;
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cancellationFailure = exception;
            PortiaTelemetry.RecordRunnerFault(nameof(PortiaWorkloadService), RunnerFaultStage.Cleanup, exception,
                _logger);
        }

        // Cancelling application callbacks may fail. The hosted lifetime still includes every
        // participant's asynchronous cleanup, and the participant's original fault stays primary.
        var completion = Task.WhenAll(tenants, coordinator);
        Exception? failure = null;
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && completion.IsCanceled)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            foreach (var secondary in completion.Exception?.InnerExceptions ?? [])
                if (!ReferenceEquals(exception, secondary))
                    ProcessorFailureException.RecordCleanup(exception, secondary);
        }

        if (failure is not null)
        {
            if (cancellationFailure is not null)
                ProcessorFailureException.RecordCleanup(failure, cancellationFailure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        if (cancellationFailure is not null)
            ExceptionDispatchInfo.Capture(cancellationFailure).Throw();

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
        OperationalSession? operationalSession = null;
        var subscribeFailures = 0;
        var resubscribeAt = DateTimeOffset.MinValue;
        IDomainEventSubscription? subscription = null;
        using var statusLifetime = statusRpc is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct);
        var statusTask = Task.CompletedTask;
        EventStreamPattern? boundPattern = null;
        Exception? failure = null;
        try
        {
            if (statusLifetime is not null)
                statusTask = MaintainStatusRpcAsync(registration, identity, statusLifetime.Token);
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
                    if (boundPattern is not null && passPattern != boundPattern)
                        throw new InvalidOperationException(
                            $"Workload '{identity.Name}' ({registration.Scope}) changed its event-stream pattern from area '{boundPattern.Area ?? "*"}' to area '{passPattern.Area ?? "*"}' across dependency-injection scopes.");
                    boundPattern = passPattern;
                    if (subscription is null)
                    {
                        // A failed subscription is retried with the same backoff as a failed pass, so a
                        // notifier that stays down is reported at a falling rate instead of every poll.
                        if (notifier is not null && _clock.GetUtcNow() >= resubscribeAt)
                        {
                            subscription = await TrySubscribeAsync(notifier, passPattern, _logger, ct)
                                .ConfigureAwait(false);
                            if (subscription is null)
                                resubscribeAt = _clock.GetUtcNow() + GetFailureDelay(registration, ++subscribeFailures);
                            else
                                subscribeFailures = 0;
                        }
                    }

                    var processing = registration.Processing;
                    if (operationalStore is not null)
                    {
                        if (operationalSession is null)
                        {
                            operationalSession = new OperationalSession(operationalStore, operationalOptions!,
                                topology!, _clock);
                            var target = new OperationalTarget(registration.Name, passPattern.ToString(),
                                new OperationalScope(identity.Tenant?.Value), processing.RebuildId);
                            await operationalSession.AcquireAsync(target,
                                registration.Descriptor.Kind,
                                new OperationalProcessingSettings(processing.MaxBatchSize, processing.MaxEventsPerPass,
                                    registration.FailureAttemptLimit, registration.PollInterval.Ticks,
                                    registration.MaximumFailureDelay.Ticks), null, ct).ConfigureAwait(false);
                        }

                        operationalSession.BeginPass();
                        var checkpoint = await registration.Descriptor.LoadCheckpoint(provider, processing, ct)
                            .ConfigureAwait(false);
                        if (!await operationalSession.PrepareAsync(checkpoint, ct).ConfigureAwait(false))
                        {
                            await Task.Delay(registration.PollInterval, _clock, ct).ConfigureAwait(false);
                            continue;
                        }

                        provider.GetRequiredService<OperationalExecution>().Session = operationalSession;
                        processing = operationalSession.BoundOptions(processing);
                        consecutiveFailures = operationalSession.State.ConsecutiveFailures;
                    }

                    var pass = await registration.Descriptor.RunPass(provider, processing, ct)
                        .ConfigureAwait(false);
                    if (operationalSession is not null)
                        await operationalSession.PassSucceededAsync(ct).ConfigureAwait(false);
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
                    if (operationalSession is not null)
                    {
                        await operationalSession.RecordFailureAsync(ex, null, ct).ConfigureAwait(false);
                        consecutiveFailures = operationalSession.State.ConsecutiveFailures;
                    }
                    else
                        consecutiveFailures++;

                    PortiaTelemetry.RecordRunnerFault(nameof(PortiaWorkloadService), RunnerFaultStage.Workload, ex,
                        _logger);
                    if (IsTerminal(ex, consecutiveFailures, registration, operationalSession))
                    {
                        terminalFailure = new WorkloadFailureException(identity, consecutiveFailures, ex);
                    }

                    if (_logger is not null)
                    {
                        LogFailureDecision(_logger, registration.Name,
                            ex is ProcessorFailureException diagnostic ? diagnostic.Stage.ToString() : "Pass",
                            consecutiveFailures, terminalFailure is null ? "retry" : "stop",
                            terminalFailure is null
                                ? GetFailureDelay(registration, consecutiveFailures)
                                : TimeSpan.Zero,
                            ex is ProcessorFailureException { Error: { } error }
                                ? error.Kind.ToString()
                                : ex.GetType().Name,
                            null);
                    }
                }

                if (terminalFailure is not null)
                {
                    if (operationalSession is null)
                        throw terminalFailure;
                    await Task.Delay(registration.PollInterval, _clock, ct).ConfigureAwait(false);
                    continue;
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
                    await ReleaseSubscriptionAsync().ConfigureAwait(false);
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
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            try
            {
                if (statusLifetime is not null)
                {
                    try
                    {
                        await statusLifetime.CancelAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        if (failure is not null)
                            ProcessorFailureException.RecordCleanup(failure, exception);
                        PortiaTelemetry.RecordRunnerFault(nameof(IProcessorStatusRpcRegistrar),
                            RunnerFaultStage.Cleanup,
                            exception, _logger);
                    }

                    await statusTask.ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (failure is not null)
            {
                ProcessorFailureException.RecordCleanup(failure, exception);
                PortiaTelemetry.RecordRunnerFault(nameof(IProcessorStatusRpcRegistrar), RunnerFaultStage.Cleanup,
                    exception, _logger);
            }
            finally
            {
                try
                {
                    await ReleaseSubscriptionAsync().ConfigureAwait(false);
                }
                finally
                {
                    PortiaTelemetry.RecordWorkload(registration.Name, telemetryScope, false, _logger);
                }
            }
        }

        async ValueTask ReleaseSubscriptionAsync()
        {
            var owned = subscription;
            subscription = null;
            if (owned is null)
                return;
            try
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (failure is not null)
                    ProcessorFailureException.RecordCleanup(failure, exception);
                PortiaTelemetry.RecordRunnerFault(nameof(PortiaWorkloadService), RunnerFaultStage.Cleanup, exception,
                    _logger);
            }
        }

        // A notification is only a wakeup, so failing to subscribe is handled like a failed wait:
        // recorded, then polled through until a later pass subscribes again. It never counts
        // against the workload's own failure limit.
        static async ValueTask<IDomainEventSubscription?> TrySubscribeAsync(
            IDomainEventNotifier notifier,
            EventStreamPattern pattern,
            ILogger? logger,
            CancellationToken token)
        {
            try
            {
                return await notifier.SubscribeAsync(pattern, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                PortiaTelemetry.RecordRunnerFault(nameof(PortiaWorkloadService), RunnerFaultStage.Notification, ex,
                    logger);
                return null;
            }
        }
    }

    async Task MaintainStatusRpcAsync(WorkloadRegistration registration, WorkloadIdentity identity,
        CancellationToken ct)
    {
        IAsyncDisposable? endpoint = null;
        var failures = 0;
        try
        {
            while (endpoint is null)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    endpoint = await statusRpc!.RegisterAsync(identity, ct).ConfigureAwait(false)
                               ?? throw new InvalidOperationException(
                                   "Processor status registration returned no endpoint.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    PortiaTelemetry.RecordRunnerFault(nameof(IProcessorStatusRpcRegistrar),
                        RunnerFaultStage.Acquisition,
                        exception, _logger);
                    failures = Math.Min(failures, 30) + 1;
                    await Task.Delay(GetFailureDelay(registration, failures), _clock, ct).ConfigureAwait(false);
                }
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (endpoint is not null)
            {
                try
                {
                    await endpoint.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    PortiaTelemetry.RecordRunnerFault(nameof(IProcessorStatusRpcRegistrar), RunnerFaultStage.Cleanup,
                        exception, _logger);
                }
            }
        }
    }

    // Retrying ends when the failure is permanent, when the attempt budget is spent, or when the journal has
    // blocked the processor on the attempt that just failed.
    static bool IsTerminal(Exception failure, int consecutiveFailures, WorkloadRegistration registration,
        OperationalSession? session) =>
        failure is ProcessorFailureException { IsTransient: false }
        || consecutiveFailures >= registration.FailureAttemptLimit
        || session is { State.Block: { } block } && session.State.Attempts[^1].Id == block.AttemptId;

    static TimeSpan GetFailureDelay(WorkloadRegistration registration, int consecutiveFailures) =>
        WorkloadFailureBackoff.Delay(registration.PollInterval, registration.MaximumFailureDelay, consecutiveFailures);
}
