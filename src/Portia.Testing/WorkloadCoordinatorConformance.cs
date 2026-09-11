using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Cntryl.Portia.Testing;

/// <summary>Reusable exclusivity, reconciliation, revocation, and shutdown checks for coordinators.</summary>
public static class WorkloadCoordinatorConformance
{
    /// <summary>Runs the complete distributed workload-coordinator conformance suite.</summary>
    /// <param name="probe">An isolated adapter capable of opening two independent workers.</param>
    /// <param name="ct">A token that can cancel verification.</param>
    /// <returns>A task that completes when every check has passed.</returns>
    /// <exception cref="ConformanceViolationException">The coordinator violates an ownership invariant.</exception>
    public static async ValueTask VerifyAsync(IWorkloadCoordinatorConformanceProbe probe,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(probe.ConvergenceTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(probe.StabilityWindow, TimeSpan.Zero);
        await VerifyExclusiveOwnershipAsync(probe, ct).ConfigureAwait(false);
        await VerifyReconciliationAsync(probe, ct).ConfigureAwait(false);
    }

    static async ValueTask VerifyExclusiveOwnershipAsync(IWorkloadCoordinatorConformanceProbe probe,
        CancellationToken ct)
    {
        await probe.ResetAsync(ct).ConfigureAwait(false);
        var identity = new WorkloadIdentity("coordinator-conformance-exclusive");
        var active = 0;
        var maximumActive = 0;
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerStarts = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var firstLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var secondLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IWorkloadCoordinatorConformanceWorker? first = null;
        IWorkloadCoordinatorConformanceWorker? second = null;
        var runs = new List<Task>(2);
        ExceptionDispatchInfo? failure = null;
        try
        {
            first = await probe.OpenWorkerAsync(ct).ConfigureAwait(false);
            second = await probe.OpenWorkerAsync(ct).ConfigureAwait(false);
            runs.Add(first.Coordinator.RunAsync(() => [identity],
                (owned, ownership) => Run(0, owned, ownership), firstLifetime.Token));
            runs.Add(second.Coordinator.RunAsync(() => [identity],
                (owned, ownership) => Run(1, owned, ownership), secondLifetime.Token));

            int initialOwner;
            try
            {
                initialOwner = await started.Task.WaitAsync(probe.ConvergenceTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new ConformanceViolationException(
                    "No worker acquired the workload within the convergence timeout.");
            }

            await Task.Delay(probe.StabilityWindow, ct).ConfigureAwait(false);
            AssertExclusive(ref maximumActive);

            var initialLifetime = initialOwner == 0 ? firstLifetime : secondLifetime;
            await initialLifetime.CancelAsync().ConfigureAwait(false);
            await ObserveCancellationAsync([runs[initialOwner]], probe.ConvergenceTimeout, ct).ConfigureAwait(false);
            try
            {
                await workerStarts[1 - initialOwner].Task.WaitAsync(probe.ConvergenceTimeout, ct)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new ConformanceViolationException(
                    "The surviving worker did not take over the workload within the convergence timeout.");
            }

            await Task.Delay(probe.StabilityWindow, ct).ConfigureAwait(false);
            AssertExclusive(ref maximumActive);
        }
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }

        failure = await CaptureCleanupFailureAsync(failure, async () =>
        {
            await firstLifetime.CancelAsync().ConfigureAwait(false);
            await secondLifetime.CancelAsync().ConfigureAwait(false);
            await ObserveCancellationAsync(runs, probe.ConvergenceTimeout, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (second is not null)
            failure = await CaptureCleanupFailureAsync(failure, second.DisposeAsync).ConfigureAwait(false);
        if (first is not null)
            failure = await CaptureCleanupFailureAsync(failure, first.DisposeAsync).ConfigureAwait(false);
        failure?.Throw();

        async Task Run(int worker, WorkloadIdentity owned, CancellationToken ownership)
        {
            _ = owned;
            var count = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, count);
            workerStarts[worker].TrySetResult();
            started.TrySetResult(worker);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ownership).ConfigureAwait(false);
            }
            finally
            {
                _ = Interlocked.Decrement(ref active);
            }
        }

        static void AssertExclusive(ref int observedMaximum)
        {
            var maximum = Volatile.Read(ref observedMaximum);
            if (maximum != 1)
            {
                throw new ConformanceViolationException(
                    $"One workload had {maximum} concurrent owners; distributed ownership must be exclusive.");
            }
        }
    }

    static async ValueTask VerifyReconciliationAsync(IWorkloadCoordinatorConformanceProbe probe,
        CancellationToken ct)
    {
        await probe.ResetAsync(ct).ConfigureAwait(false);
        var firstIdentity = new WorkloadIdentity("coordinator-conformance-first");
        var secondIdentity = new WorkloadIdentity("coordinator-conformance-second");
        WorkloadIdentity[] snapshot = [firstIdentity];
        var starts = new ConcurrentDictionary<WorkloadIdentity, int>();
        var active = new ConcurrentDictionary<WorkloadIdentity, int>();
        var started = new ConcurrentDictionary<WorkloadIdentity, TaskCompletionSource>();
        var stopped = new ConcurrentDictionary<WorkloadIdentity, TaskCompletionSource>();
        using var firstLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var secondLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IWorkloadCoordinatorConformanceWorker? first = null;
        IWorkloadCoordinatorConformanceWorker? second = null;
        var runs = new List<Task>(2);
        ExceptionDispatchInfo? failure = null;
        try
        {
            first = await probe.OpenWorkerAsync(ct).ConfigureAwait(false);
            second = await probe.OpenWorkerAsync(ct).ConfigureAwait(false);
            runs.Add(first.Coordinator.RunAsync(() => Volatile.Read(ref snapshot), Run, firstLifetime.Token));
            runs.Add(second.Coordinator.RunAsync(() => Volatile.Read(ref snapshot), Run, secondLifetime.Token));

            try
            {
                await Signal(started, firstIdentity).Task.WaitAsync(probe.ConvergenceTimeout, ct)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new ConformanceViolationException(
                    "No worker acquired the initial workload within the convergence timeout.");
            }

            await Task.Delay(probe.StabilityWindow, ct).ConfigureAwait(false);
            if (starts.GetValueOrDefault(firstIdentity) != 1
                || Signal(stopped, firstIdentity).Task.IsCompleted
                || active.GetValueOrDefault(firstIdentity) != 1)
            {
                throw new ConformanceViolationException(
                    "Unchanged reconciliation did not retain one uninterrupted owner.");
            }

            Volatile.Write(ref snapshot, [secondIdentity]);
            await Signal(stopped, firstIdentity).Task.WaitAsync(probe.ConvergenceTimeout, ct).ConfigureAwait(false);
            await Signal(started, secondIdentity).Task.WaitAsync(probe.ConvergenceTimeout, ct).ConfigureAwait(false);
            await Task.Delay(probe.StabilityWindow, ct).ConfigureAwait(false);
            if (starts.GetValueOrDefault(firstIdentity) != 1 || active.GetValueOrDefault(firstIdentity) != 0)
                throw new ConformanceViolationException("A removed workload was reacquired after revocation.");
            if (starts.GetValueOrDefault(secondIdentity) != 1
                || Signal(stopped, secondIdentity).Task.IsCompleted
                || active.GetValueOrDefault(secondIdentity) != 1)
            {
                throw new ConformanceViolationException(
                    "Replacement workload did not retain one uninterrupted owner.");
            }
        }
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }

        failure = await CaptureCleanupFailureAsync(failure, async () =>
        {
            await firstLifetime.CancelAsync().ConfigureAwait(false);
            await secondLifetime.CancelAsync().ConfigureAwait(false);
            await ObserveCancellationAsync(runs, probe.ConvergenceTimeout, ct).ConfigureAwait(false);
            if (active.Values.Any(static count => count != 0))
                throw new ConformanceViolationException("Coordinator shutdown returned before owned work stopped.");
        }).ConfigureAwait(false);
        if (second is not null)
            failure = await CaptureCleanupFailureAsync(failure, second.DisposeAsync).ConfigureAwait(false);
        if (first is not null)
            failure = await CaptureCleanupFailureAsync(failure, first.DisposeAsync).ConfigureAwait(false);
        failure?.Throw();

        async Task Run(WorkloadIdentity identity, CancellationToken ownership)
        {
            _ = starts.AddOrUpdate(identity, 1, static (_, count) => count + 1);
            _ = active.AddOrUpdate(identity, 1, static (_, count) => count + 1);
            Signal(started, identity).TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ownership).ConfigureAwait(false);
            }
            finally
            {
                _ = active.AddOrUpdate(identity, 0, static (_, count) => count - 1);
                Signal(stopped, identity).TrySetResult();
            }
        }
    }

    static TaskCompletionSource Signal(
        ConcurrentDictionary<WorkloadIdentity, TaskCompletionSource> signals,
        WorkloadIdentity identity) => signals.GetOrAdd(identity,
        static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    static async ValueTask ObserveCancellationAsync(IEnumerable<Task> runs, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(runs).WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            throw new ConformanceViolationException(
                "Coordinator shutdown did not complete within the convergence timeout.");
        }
    }

    static async ValueTask<ExceptionDispatchInfo?> CaptureCleanupFailureAsync(
        ExceptionDispatchInfo? failure,
        Func<ValueTask> cleanup)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception exception) when (failure is null)
        {
            return ExceptionDispatchInfo.Capture(exception);
        }
        catch (Exception)
        {
            // Preserve the primary conformance violation instead of replacing it with cleanup fallout.
        }

        return failure;
    }

    static void UpdateMaximum(ref int maximum, int value)
    {
        var observed = Volatile.Read(ref maximum);
        while (value > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, value, observed);
            if (previous == observed)
                return;
            observed = previous;
        }
    }
}
