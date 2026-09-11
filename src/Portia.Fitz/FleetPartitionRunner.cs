using Cntryl.Fitz.Abstractions.Domains.Lease;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
///     Assigns fixed partitions by rendezvous hashing over renewable fleet membership, then acquires each assigned
///     lease.
/// </summary>
/// <param name="leases">Acquires partition leases.</param>
/// <param name="membership">Owns this worker's renewable membership and inventory.</param>
/// <param name="logger">Reports membership and partition failures and assignment changes.</param>
/// <param name="timeProvider">Schedules reconciliation and cancellable retry backoff.</param>
public sealed partial class FleetPartitionRunner(
    IPartitionLeaseCompetitor leases,
    IFleetMembership membership,
    ILogger<FleetPartitionRunner>? logger = null,
    TimeProvider? timeProvider = null)
{
    /// <summary>Gets the ceiling a retry delay grows to after repeated failures.</summary>
    public static readonly TimeSpan MaximumRetryBackoff = TimeSpan.FromSeconds(30);

    static readonly TimeSpan InitialRetryBackoff = TimeSpan.FromSeconds(1);

    // Doubling from one second reaches the ceiling well inside this many attempts.
    const int MaximumBackoffAttempt = 16;

    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    readonly IPartitionLeaseCompetitor _leases = leases ?? throw new ArgumentNullException(nameof(leases));
    readonly ILogger<FleetPartitionRunner>? _logger = logger;
    readonly IFleetMembership _membership = membership ?? throw new ArgumentNullException(nameof(membership));

    /// <summary>
    ///     Runs assigned partitions until cancellation. Every fleet worker must use the same selector, partitions, and
    ///     algorithm.
    /// </summary>
    /// <param name="partitions">The fixed set of exact lease routes outside the membership area.</param>
    /// <param name="onPartitionAcquired">Runs while the partition lease is held.</param>
    /// <param name="options">Membership, timing, and optional stable worker identity.</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>The complete lifetime, including observation of all revoked work.</returns>
    public Task RunAsync(IReadOnlyCollection<string> partitions,
        Func<string, CancellationToken, Task> onPartitionAcquired,
        FleetRunOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onPartitionAcquired);
        options.Validate(partitions);
        var runOptions = options with { WorkerId = options.WorkerId ?? Guid.NewGuid().ToString("D") };
        return RunMembershipAsync(() => partitions, onPartitionAcquired, runOptions, ct);
    }

    /// <summary>Reconciles a changing partition snapshot without restarting retained assignments.</summary>
    /// <param name="partitions">Reports the current set of exact lease routes outside the membership area.</param>
    /// <param name="onPartitionAcquired">Runs while the partition lease is held.</param>
    /// <param name="options">Membership, timing, and optional stable worker identity.</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>The complete lifetime, including observation of all revoked work.</returns>
    public Task RunAsync(Func<IReadOnlyCollection<string>> partitions,
        Func<string, CancellationToken, Task> onPartitionAcquired,
        FleetRunOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(onPartitionAcquired);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(partitions());
        return RunMembershipAsync(partitions, onPartitionAcquired,
            options with { WorkerId = options.WorkerId ?? Guid.NewGuid().ToString("D") }, ct);
    }

    async Task RunMembershipAsync(Func<IReadOnlyCollection<string>> partitions,
        Func<string, CancellationToken, Task> callback,
        FleetRunOptions options, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _membership.RunAsync(options, (observer, membershipCt) =>
                    ReconcileAsync(partitions, callback, options, observer, membershipCt), ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                {
                    Fault(RunnerFaultStage.Acquisition);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (WorkloadFailureException)
            {
                throw;
            }
            catch (FleetPartitionTerminationTimeoutException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Fault(RunnerFaultStage.Acquisition, ex);
            }

            attempt = NextAttempt(attempt);
            if (!await BackoffAsync(attempt, ct).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    async Task ReconcileAsync(Func<IReadOnlyCollection<string>> partitions,
        Func<string, CancellationToken, Task> callback,
        FleetRunOptions options, ILeaseInventoryObserver observer, CancellationToken ct)
    {
        var active = new Dictionary<string, PartitionRun>(StringComparer.Ordinal);
        var assignments = new FleetAssignmentCache();
        var prefix = options.MembershipSelector[..^1];
        var unavailable = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var ready = observer.IsReady;
                var snapshot = observer.View;
                var workers = snapshot.Keys.Where(route => route.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(route => route[prefix.Length..]).Where(FleetRunOptions.IsSegment).ToArray();
                ready &= observer.IsReady && workers.Contains(options.WorkerId, StringComparer.Ordinal);
                if (!ready && !unavailable)
                {
                    Fault(RunnerFaultStage.Acquisition);
                }

                unavailable = !ready;
                var current = partitions();
                options.Validate(current);
                var assigned = ready
                    ? assignments.GetAssignments(current, workers, options.WorkerId!)
                    : new HashSet<string>(StringComparer.Ordinal);
                var revoked = active.Keys.Where(partition => !assigned.Contains(partition)).ToArray();
                foreach (var partition in revoked)
                    Cancel(active[partition].Cancellation);
                await ObserveAsync([.. revoked.Select(partition => (partition, active[partition]))],
                    options.PartitionStopTimeout).ConfigureAwait(false);
                foreach (var partition in revoked)
                {
                    _ = active.Remove(partition);
                    PortiaTelemetry.RecordFleetAssignment(options.WorkerId!, partition, false, _logger);
                }

                ct.ThrowIfCancellationRequested();
                foreach (var partition in assigned)
                {
                    if (active.ContainsKey(partition))
                    {
                        continue;
                    }

                    ct.ThrowIfCancellationRequested();
                    var cancellation = new CancellationTokenSource();
                    var registration = ct.Register(() => Cancel(cancellation));
                    active.Add(partition, new PartitionRun(cancellation,
                        Task.Run(() => CompeteAsync(partition, callback, options.TtlSeconds, cancellation.Token),
                            CancellationToken.None), registration));
                    PortiaTelemetry.RecordFleetAssignment(options.WorkerId!, partition, true, _logger);
                }

                foreach (var run in active.Values.Where(run => run.Task.IsCompleted))
                    await run.Task.ConfigureAwait(false);
                await Task.Delay(options.ReconciliationInterval, _clock, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var (partition, run) in active)
                Cancel(run.Cancellation);
            await ObserveAsync([.. active.Select(pair => (pair.Key, pair.Value))],
                options.PartitionStopTimeout).ConfigureAwait(false);
        }
    }

    async Task CompeteAsync(string partition, Func<string, CancellationToken, Task> callback,
        ulong ttl, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var acquired = false;
            try
            {
                await _leases.WithLeaseAsync(partition, ttl, leaseCt =>
                {
                    acquired = true;
                    return new ValueTask(callback(partition, leaseCt));
                }, new LeaseExecutionOptions { WaitForAvailability = true }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (WorkloadFailureException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Fault(acquired ? RunnerFaultStage.Workload : RunnerFaultStage.Acquisition, ex);
            }

            // Holding the lease means the cycle made progress, so the next retry starts short
            // again rather than inheriting the backoff an earlier outage had grown.
            attempt = acquired ? 1 : NextAttempt(attempt);
            if (!await BackoffAsync(attempt, ct).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    // A flat delay retried the whole fleet in lockstep, so a broker that had just failed was hit
    // again by every worker at once. The delay doubles to a ceiling, and half of it is randomized
    // so recovering workers spread out instead of rebuilding the burst that caused the outage.
    // Past the ceiling the count no longer changes the delay, so it stops growing rather than
    // running to overflow in a process that retries for a very long time.
    static int NextAttempt(int attempt) => attempt >= MaximumBackoffAttempt ? attempt : attempt + 1;

    async Task<bool> BackoffAsync(int attempt, CancellationToken ct)
    {
        // The caller's counter is clamped as it grows, so a runner that retries for long enough to
        // overflow it cannot end up shifting by a negative exponent and computing a negative delay.
        var ceiling = Math.Min(InitialRetryBackoff.Ticks * (1L << (attempt - 1)), MaximumRetryBackoff.Ticks);
        var delay = TimeSpan.FromTicks((ceiling / 2) + Random.Shared.NextInt64(ceiling / 2));
        try
        {
            await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
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
            Fault(RunnerFaultStage.Cleanup, ex);
        }
    }

    async Task ObserveAsync((string Partition, PartitionRun Run)[] runs, TimeSpan timeout)
    {
        runs = [.. runs.Where(item => !item.Run.ObservationClaimed)];
        if (runs.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(runs.Select(item => item.Run.Task)).WaitAsync(timeout, _clock).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            (string Partition, PartitionRun Run)[] timedOut = [.. runs.Where(item => !item.Run.Task.IsCompleted)];
            foreach (var (partition, run) in runs)
            {
                if (!run.TryClaimObservation())
                {
                    continue;
                }

                if (run.Task.IsCompleted)
                {
                    await ObserveCompletedAsync(run).ConfigureAwait(false);
                }
                else
                {
                    _ = ObserveLateAsync(run);
                }
            }

            var exception = new FleetPartitionTerminationTimeoutException(
                [.. timedOut.Select(item => item.Partition)], timeout);
            PortiaTelemetry.RecordRunnerFault(nameof(FleetPartitionRunner), RunnerFaultStage.Cleanup, exception);
            if (_logger is not null)
            {
                LogPartitionTerminationTimeout(_logger, string.Join(",", exception.Partitions), exception);
            }

            throw exception;
        }
        catch (Exception)
        {
            // The complete set has stopped. Observe each task below so callback failures and
            // cleanup remain isolated and reported through the existing runner-fault contract.
        }

        foreach (var (partition, run) in runs)
        {
            if (run.TryClaimObservation())
            {
                await ObserveCompletedAsync(run).ConfigureAwait(false);
            }
        }
    }

    async Task ObserveLateAsync(PartitionRun run) =>
        await ObserveCompletedAsync(run).ConfigureAwait(false);

    async Task ObserveCompletedAsync(PartitionRun run)
    {
        try
        {
            await run.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Fault(RunnerFaultStage.Cleanup, ex);
        }
        finally
        {
            run.Registration.Dispose();
            run.Cancellation.Dispose();
        }
    }

    void Fault(RunnerFaultStage stage, Exception? exception = null) =>
        PortiaTelemetry.RecordRunnerFault(nameof(FleetPartitionRunner), stage, exception, _logger);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Error,
        Message =
            "Portia FleetPartitionRunner fault at cleanup (FleetPartitionTerminationTimeoutException); partitions: {Partitions}")]
    static partial void LogPartitionTerminationTimeout(ILogger logger, string partitions, Exception exception);

    sealed class PartitionRun(
        CancellationTokenSource cancellation,
        Task task,
        CancellationTokenRegistration registration)
    {
        int _observationClaimed;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; } = task;
        public CancellationTokenRegistration Registration { get; } = registration;
        public bool ObservationClaimed => Volatile.Read(ref _observationClaimed) != 0;
        public bool TryClaimObservation() => Interlocked.Exchange(ref _observationClaimed, 1) == 0;
    }
}
