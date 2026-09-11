using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
///     Runs one instance of a per-tenant component for every currently active tenant, starting a new
///     instance as tenants are added and stopping it as they're removed — the tenant control plane.
///     Deliberately decoupled from <see cref="Projector" />/<see cref="Reactor" />
///     specifics: a caller supplies what "start" and "stop" mean for one tenant (typically
///     constructing a tenant-scoped <see cref="EventStreamPattern" /> — using the tenant's own id as
///     its realm, the same convention <see cref="TenantId" /> documents — and driving
///     <see cref="ProjectorRunner" />/<see cref="ReactorRunner" /> in a loop against it), so this
///     works the same way for any per-tenant component, not just projectors and reactors.
///     This is orthogonal to fleet distribution: fleet decides which worker process runs a given
///     component; this decides which tenants that component runs for, on whichever worker it's
///     already running on. Every worker runs the same code, so the two concerns compose freely.
/// </summary>
/// <param name="tenantDirectory">Reports active tenants and lifecycle changes.</param>
/// <param name="logger">
///     Reports a tenant's start callback faulting even when nothing is listening to
///     <see cref="PortiaTelemetry.ActivitySource" />. Supply it explicitly, or configure
///     Microsoft.Extensions.Logging with at least one provider before resolving the runner through
///     DI; a bare <c>ServiceCollection</c> registration does not create or emit logs.
/// </param>
/// <param name="timeProvider">Schedules reconnect and workload restart delays.</param>
/// <param name="restartInterval">Positive delay between attempts; defaults to one second.</param>
/// <param name="shutdownGrace">
///     Positive total time allowed for active-tenant cleanup during shutdown; defaults to five seconds.
/// </param>
public sealed class MultiTenantRunner(
    ITenantDirectory tenantDirectory,
    ILogger<MultiTenantRunner>? logger = null,
    TimeProvider? timeProvider = null,
    TimeSpan? restartInterval = null,
    TimeSpan? shutdownGrace = null)
{
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    readonly ILogger<MultiTenantRunner>? _logger = logger;
    readonly TimeSpan _restartInterval = GetRestartInterval(restartInterval);
    readonly TimeSpan _shutdownGrace = GetShutdownGrace(shutdownGrace);
    readonly TimeSpan _tenantStopTimeout = TimeSpan.FromSeconds(5);

    readonly ITenantDirectory _tenantDirectory =
        tenantDirectory ?? throw new ArgumentNullException(nameof(tenantDirectory));

    /// <summary>Creates a runner using the default five-second shutdown grace.</summary>
    /// <param name="tenantDirectory">Reports active tenants and lifecycle changes.</param>
    /// <param name="logger">Reports runner faults when configured.</param>
    /// <param name="timeProvider">Schedules reconnect and workload restart delays.</param>
    /// <param name="restartInterval">Positive delay between attempts; defaults to one second.</param>
    public MultiTenantRunner(
        ITenantDirectory tenantDirectory,
        ILogger<MultiTenantRunner>? logger,
        TimeProvider? timeProvider,
        TimeSpan? restartInterval)
        : this(tenantDirectory, logger, timeProvider, restartInterval, null)
    {
    }

    /// <summary>Creates a runner using explicit recovery and cleanup options.</summary>
    /// <param name="tenantDirectory">Reports active tenants and lifecycle changes.</param>
    /// <param name="options">Recovery, shutdown, and normal-removal durations.</param>
    public MultiTenantRunner(ITenantDirectory tenantDirectory, MultiTenantRunnerOptions options)
        : this(tenantDirectory, options, null, null)
    {
    }

    /// <summary>Creates a runner using explicit recovery and cleanup options.</summary>
    /// <param name="tenantDirectory">Reports active tenants and lifecycle changes.</param>
    /// <param name="options">Recovery, shutdown, and normal-removal durations.</param>
    /// <param name="logger">Reports runner faults when configured.</param>
    /// <param name="timeProvider">Schedules delays and cleanup deadlines.</param>
    public MultiTenantRunner(
        ITenantDirectory tenantDirectory,
        MultiTenantRunnerOptions options,
        ILogger<MultiTenantRunner>? logger,
        TimeProvider? timeProvider)
        : this(tenantDirectory, logger, timeProvider,
            GetPositive(options, static value => value.RestartInterval, nameof(options.RestartInterval)),
            GetPositive(options, static value => value.ShutdownGrace, nameof(options.ShutdownGrace)))
    {
        _tenantStopTimeout = GetPositive(options, static value => value.TenantStopTimeout,
            nameof(options.TenantStopTimeout));
    }

    /// <summary>
    ///     Starts an instance for every currently active tenant, then keeps starting and stopping
    ///     instances as tenants are added and removed, until cancellation is requested. A tenant
    ///     that's already running when an "added" change arrives for it (e.g. it was already picked
    ///     up at startup) is left alone — added is idempotent, not a restart.
    /// </summary>
    /// <param name="onTenantStarted">
    ///     Runs for a tenant once it becomes active. Runs until the
    ///     tenant is removed or the whole run is cancelled — typically a loop driving a runner
    ///     against a tenant-scoped pattern, not a single pass.
    /// </param>
    /// <param name="onTenantStopped">
    ///     Runs once for a tenant after it's removed and its
    ///     <paramref name="onTenantStarted" /> task has been cancelled and observed. Normal removal
    ///     receives the remainder of one per-tenant stop deadline. Host shutdown supplies a fresh
    ///     token with one shared bounded grace interval, so cooperative cleanup can finish without
    ///     allowing any callback to hang shutdown.
    /// </param>
    /// <param name="ct">
    ///     A token that can cancel the operation. Cancels every active tenant's
    ///     <paramref name="onTenantStarted" /> task and stops watching for further changes.
    /// </param>
    /// <returns>A task representing the run.</returns>
    public async Task RunAsync(
        Func<TenantId, CancellationToken, Task> onTenantStarted,
        Func<TenantId, CancellationToken, Task> onTenantStopped,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onTenantStarted);
        ArgumentNullException.ThrowIfNull(onTenantStopped);

        var active = new ConcurrentDictionary<TenantId, TenantRun>();
        await using var cursor = _tenantDirectory is IResumableTenantDirectory resumable
            ? await resumable.OpenCursorAsync(ct).ConfigureAwait(false)
            : null;

        try
        {
            // A transient failure from the directory's live watch stream (a network blip in a
            // real implementation) must not permanently end tenant management for the rest of
            // the process — the same resilience QueueRunner/RequestNotificationRunner already have for
            // their own live streams.
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (cursor is not null)
                    {
                        await foreach (var change in cursor.ReadAsync(ct).WithCancellation(ct).ConfigureAwait(false))
                            await ApplyChangeAsync(change, onTenantStarted, onTenantStopped, active, ct)
                                .ConfigureAwait(false);
                    }
                    else
                    {
                        // The legacy contract has no cursor. Re-reading its complete snapshot on
                        // reconnect closes the gap between separately opened watch streams.
                        var snapshot = new HashSet<TenantId>();
                        await foreach (var tenantId in _tenantDirectory.GetActiveTenantsAsync(ct).WithCancellation(ct)
                                           .ConfigureAwait(false))
                        {
                            _ = snapshot.Add(tenantId);
                            Start(tenantId, onTenantStarted, active, ct);
                        }

                        foreach (var tenantId in active.Keys.ToArray())
                        {
                            if (!snapshot.Contains(tenantId))
                            {
                                await StopRemovedTenantAsync(tenantId, onTenantStopped, active, ct)
                                    .ConfigureAwait(false);
                            }
                        }

                        await foreach (var change in _tenantDirectory.WatchAsync(ct).WithCancellation(ct)
                                           .ConfigureAwait(false))
                        {
                            await ApplyChangeAsync(change, onTenantStarted, onTenantStopped, active, ct)
                                .ConfigureAwait(false);
                        }
                    }

                    // WatchAsync's enumerable ending on its own, without cancellation, is just as
                    // unexpected as it throwing — a healthy watch stream is meant to run for the
                    // life of this call. Reconnect rather than silently stop watching forever,
                    // and still report it: a silently-repeating clean EOF is exactly the kind of
                    // thing that must be visible, not just handled.
                    if (ct.IsCancellationRequested)
                        break;

                    PortiaTelemetry.RecordRunnerFault(nameof(MultiTenantRunner), RunnerFaultStage.Watch, null, _logger);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (TenantStopTimeoutException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    PortiaTelemetry.RecordRunnerFault(nameof(MultiTenantRunner), RunnerFaultStage.Watch, ex, _logger);
                }

                // Reached after either a faulted or a normally-completed watch — always back off
                // before reconnecting. Without this on the normal-completion path, a directory
                // whose watch keeps ending cleanly (rather than throwing) would spin this loop as
                // fast as GetActiveTenantsAsync/WatchAsync allow, with no pause at all.
                try
                {
                    await Task.Delay(_restartInterval, _clock, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            foreach (var (tenantId, run) in active)
                Cancel(run.Cancellation);
            using var shutdown = new CancellationTokenSource(_shutdownGrace);
            foreach (var tenantId in active.Keys.ToArray())
                await StopAsync(tenantId, onTenantStopped, active, false, shutdown.Token).ConfigureAwait(false);
        }
    }

    async ValueTask ApplyChangeAsync(
        TenantLifecycleChange change,
        Func<TenantId, CancellationToken, Task> onTenantStarted,
        Func<TenantId, CancellationToken, Task> onTenantStopped,
        ConcurrentDictionary<TenantId, TenantRun> active,
        CancellationToken ct)
    {
        if (change.Kind == TenantLifecycleChangeKind.Added)
        {
            Start(change.TenantId, onTenantStarted, active, ct);
        }
        else if (change.Kind == TenantLifecycleChangeKind.Removed)
        {
            await StopRemovedTenantAsync(change.TenantId, onTenantStopped, active, ct).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException($"Unrecognized tenant lifecycle change kind '{change.Kind}'.");
        }
    }

    async ValueTask StopRemovedTenantAsync(
        TenantId tenantId,
        Func<TenantId, CancellationToken, Task> onTenantStopped,
        ConcurrentDictionary<TenantId, TenantRun> active,
        CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(_tenantStopTimeout, _clock);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        await StopAsync(tenantId, onTenantStopped, active, true, budget.Token, deadline.Token, ct)
            .ConfigureAwait(false);
    }

    void Start(
        TenantId tenantId,
        Func<TenantId, CancellationToken, Task> onTenantStarted,
        ConcurrentDictionary<TenantId, TenantRun> active,
        CancellationToken ct)
    {
        // RunAsync only ever calls Start/StopAsync sequentially from its own loops — never
        // concurrently with each other — so this reservation-then-fill isn't racing anything;
        // it's just how a TenantRun's Task can reference the same instance's own Cancellation.
        ct.ThrowIfCancellationRequested();
        if (active.ContainsKey(tenantId))
            return;

        var cts = new CancellationTokenSource();
        var registration = ct.Register(() => Cancel(cts));
        var task = Task.Run(() => RunTenantAsync(tenantId, onTenantStarted, cts.Token), cts.Token);

        active[tenantId] = new TenantRun(cts, task, registration);
    }

    async Task RunTenantAsync(TenantId tenantId, Func<TenantId, CancellationToken, Task> onTenantStarted,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await onTenantStarted(tenantId, ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                {
                    PortiaTelemetry.RecordRunnerFault(nameof(MultiTenantRunner), RunnerFaultStage.Workload,
                        logger: _logger);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PortiaTelemetry.RecordRunnerFault(nameof(MultiTenantRunner), RunnerFaultStage.Workload, ex, _logger);
            }

            try
            {
                await Task.Delay(_restartInterval, _clock, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    static TimeSpan GetRestartInterval(TimeSpan? restartInterval)
    {
        var interval = restartInterval ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero, nameof(restartInterval));
        return interval;
    }

    static TimeSpan GetShutdownGrace(TimeSpan? shutdownGrace)
    {
        var grace = shutdownGrace ?? TimeSpan.FromSeconds(5);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(grace, TimeSpan.Zero, nameof(shutdownGrace));
        return grace;
    }

    static TimeSpan GetPositive(
        MultiTenantRunnerOptions options,
        Func<MultiTenantRunnerOptions, TimeSpan> select,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = select(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, parameterName);
        return value;
    }

    async ValueTask StopAsync(
        TenantId tenantId,
        Func<TenantId, CancellationToken, Task> onTenantStopped,
        ConcurrentDictionary<TenantId, TenantRun> active,
        bool throwOnTimeout,
        CancellationToken stopToken,
        CancellationToken timeoutToken = default,
        CancellationToken hostToken = default)
    {
        if (!active.TryRemove(tenantId, out var run))
            return;

        Cancel(run.Cancellation);

        try
        {
            await run.Task.WaitAsync(stopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            _ = ObserveLateStopAsync(run.Task);
            if (throwOnTimeout && timeoutToken.IsCancellationRequested && !hostToken.IsCancellationRequested)
                throw new TenantStopTimeoutException(tenantId, _tenantStopTimeout);
        }
        catch (OperationCanceledException)
        {
            // Expected — the tenant's own run observed the cancellation it was just given.
        }
        catch (Exception ex)
        {
            ReportCleanupFault(ex);
        }
        finally
        {
            run.Registration.Dispose();
            run.Cancellation.Dispose();
        }

        Task? stop = null;
        try
        {
            stop = onTenantStopped(tenantId, stopToken);
            await stop.WaitAsync(stopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            if (stop is not null)
                _ = ObserveLateStopAsync(stop);
            if (throwOnTimeout && timeoutToken.IsCancellationRequested && !hostToken.IsCancellationRequested)
                throw new TenantStopTimeoutException(tenantId, _tenantStopTimeout);
        }
        catch (Exception ex)
        {
            ReportCleanupFault(ex);
        }
    }

    async Task ObserveLateStopAsync(Task stop)
    {
        try
        {
            await stop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ReportCleanupFault(ex);
        }
    }

    void Cancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (Exception ex)
        {
            ReportCleanupFault(ex);
        }
    }

    void ReportCleanupFault(Exception exception)
    {
        var errors = exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [exception];
        foreach (var error in errors)
            PortiaTelemetry.RecordRunnerFault(nameof(MultiTenantRunner), RunnerFaultStage.Cleanup, error, _logger);
    }

    sealed record TenantRun(
        CancellationTokenSource Cancellation,
        Task Task,
        CancellationTokenRegistration Registration);
}
