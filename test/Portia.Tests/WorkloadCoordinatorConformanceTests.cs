using System.Collections.Concurrent;

namespace Cntryl.Portia;

/// <summary>Verifies the reusable distributed coordinator conformance contract.</summary>
public sealed class WorkloadCoordinatorConformanceTests
{
    /// <summary>A shared exclusive owner registry satisfies the black-box contract.</summary>
    [Fact]
    public async Task ShouldAcceptExclusiveCoordinator() =>
        await WorkloadCoordinatorConformance.VerifyAsync(new SharedProbe(false));

    /// <summary>Independent workers that both claim every workload are rejected.</summary>
    [Fact]
    public async Task ShouldRejectDuplicateOwners()
    {
        var exception = await Assert.ThrowsAsync<ConformanceViolationException>(() =>
            WorkloadCoordinatorConformance.VerifyAsync(new SharedProbe(true)).AsTask());

        Assert.Contains("concurrent owners", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A surviving worker that never acquires released work is rejected.</summary>
    [Fact]
    public async Task ShouldRejectMissingTakeover()
    {
        var exception = await Assert.ThrowsAsync<ConformanceViolationException>(() =>
            WorkloadCoordinatorConformance.VerifyAsync(new MissingTakeoverProbe()).AsTask());

        Assert.Contains("take over", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Releasing a lease before its old callback stops is rejected after takeover.</summary>
    [Fact]
    public async Task ShouldRejectOverlapDuringTakeover()
    {
        var exception = await Assert.ThrowsAsync<ConformanceViolationException>(() =>
            WorkloadCoordinatorConformance.VerifyAsync(new SharedProbe(false, true)).AsTask());

        Assert.Contains("concurrent owners", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A retained workload that silently stops is not considered stable.</summary>
    [Fact]
    public async Task ShouldRejectStoppedRetainedWorkload()
    {
        var exception = await Assert.ThrowsAsync<ConformanceViolationException>(() =>
            WorkloadCoordinatorConformance.VerifyAsync(
                new ReconciliationFailureProbe(ReconciliationFailure.DropRetained)).AsTask());

        Assert.Contains("Unchanged reconciliation", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A revoked workload reacquired on a later reconciliation is rejected.</summary>
    [Fact]
    public async Task ShouldRejectReacquiredRevokedWorkload()
    {
        var exception = await Assert.ThrowsAsync<ConformanceViolationException>(() =>
            WorkloadCoordinatorConformance.VerifyAsync(
                new ReconciliationFailureProbe(ReconciliationFailure.ReacquireRevoked)).AsTask());

        Assert.Contains("reacquired", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A coordinator that never starts work reports initial acquisition, not takeover.</summary>
    [Fact]
    public async Task ShouldReportMissingInitialAcquisition()
    {
        var exception = await Assert.ThrowsAsync<ConformanceViolationException>(() =>
            WorkloadCoordinatorConformance.VerifyAsync(new NoStartProbe()).AsTask());

        Assert.Contains("No worker acquired", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A coordinator that ignores shutdown cancellation is rejected within a bounded time.</summary>
    [Fact]
    public async Task ShouldBoundCancellationCleanup()
    {
        var verification = WorkloadCoordinatorConformance.VerifyAsync(new IgnoringCancellationProbe()).AsTask();
        var exception = await Assert.ThrowsAsync<ConformanceViolationException>(() =>
            verification.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Contains("shutdown", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A cleanup timeout does not replace an earlier ownership violation.</summary>
    [Fact]
    public async Task ShouldPreservePrimaryViolationWhenCleanupAlsoFails()
    {
        var exception = await Assert.ThrowsAsync<ConformanceViolationException>(() =>
            WorkloadCoordinatorConformance.VerifyAsync(new IgnoringCancellationProbe(true)).AsTask());

        Assert.Contains("concurrent owners", exception.Message, StringComparison.Ordinal);
    }

    sealed class SharedProbe(bool duplicateOwners, bool releaseBeforeStop = false)
        : IWorkloadCoordinatorConformanceProbe
    {
        readonly ConcurrentDictionary<WorkloadIdentity, object> _owners = new();
        public TimeSpan ConvergenceTimeout => TimeSpan.FromSeconds(2);
        public TimeSpan StabilityWindow => TimeSpan.FromMilliseconds(50);

        public ValueTask ResetAsync(CancellationToken ct = default)
        {
            _owners.Clear();
            return ValueTask.CompletedTask;
        }

        public ValueTask<IWorkloadCoordinatorConformanceWorker> OpenWorkerAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IWorkloadCoordinatorConformanceWorker>(
                new CoordinatorWorker(new SharedCoordinator(_owners, duplicateOwners, releaseBeforeStop)));
    }

    sealed class SharedCoordinator(
        ConcurrentDictionary<WorkloadIdentity, object> owners,
        bool duplicateOwners,
        bool releaseBeforeStop = false)
        : IWorkloadCoordinator
    {
        readonly object _owner = new();

        public async Task RunAsync(Func<IReadOnlyCollection<WorkloadIdentity>> workloads,
            Func<WorkloadIdentity, CancellationToken, Task> run, CancellationToken ct = default)
        {
            var active = new Dictionary<WorkloadIdentity, (CancellationTokenSource Cancellation, Task Run)>();
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var current = workloads().ToHashSet();
                    foreach (var identity in active.Keys.Where(identity => !current.Contains(identity)).ToArray())
                        await ReleaseAsync(identity).ConfigureAwait(false);
                    foreach (var identity in current)
                    {
                        if (active.ContainsKey(identity) || !duplicateOwners && !owners.TryAdd(identity, _owner))
                            continue;
                        var cancellation = releaseBeforeStop
                            ? new CancellationTokenSource()
                            : CancellationTokenSource.CreateLinkedTokenSource(ct);
                        active.Add(identity, (cancellation, run(identity, cancellation.Token)));
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(5), ct).ConfigureAwait(false);
                }
            }
            finally
            {
                foreach (var identity in active.Keys.ToArray())
                    await ReleaseAsync(identity).ConfigureAwait(false);
            }

            async Task ReleaseAsync(WorkloadIdentity identity)
            {
                var owned = active[identity];
                active.Remove(identity);
                if (releaseBeforeStop && !duplicateOwners)
                {
                    _ = owners.TryRemove(new KeyValuePair<WorkloadIdentity, object>(identity, _owner));
                    await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None).ConfigureAwait(false);
                }
                await owned.Cancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await owned.Run.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (owned.Cancellation.IsCancellationRequested)
                {
                }
                finally
                {
                    if (!duplicateOwners && !releaseBeforeStop)
                        _ = owners.TryRemove(new KeyValuePair<WorkloadIdentity, object>(identity, _owner));
                    owned.Cancellation.Dispose();
                }
            }
        }
    }

    sealed class MissingTakeoverProbe : IWorkloadCoordinatorConformanceProbe
    {
        int _workers;
        public TimeSpan ConvergenceTimeout => TimeSpan.FromMilliseconds(500);
        public TimeSpan StabilityWindow => TimeSpan.FromMilliseconds(100);
        public ValueTask ResetAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<IWorkloadCoordinatorConformanceWorker> OpenWorkerAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IWorkloadCoordinatorConformanceWorker>(new CoordinatorWorker(
                Interlocked.Increment(ref _workers) == 1
                    ? new FirstOwnerCoordinator()
                    : new InertCoordinator()));
    }

    sealed class NoStartProbe : IWorkloadCoordinatorConformanceProbe
    {
        public TimeSpan ConvergenceTimeout => TimeSpan.FromMilliseconds(500);
        public TimeSpan StabilityWindow => TimeSpan.FromMilliseconds(100);
        public ValueTask ResetAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<IWorkloadCoordinatorConformanceWorker> OpenWorkerAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IWorkloadCoordinatorConformanceWorker>(
                new CoordinatorWorker(new InertCoordinator()));
    }

    enum ReconciliationFailure
    {
        DropRetained,
        ReacquireRevoked
    }

    sealed class ReconciliationFailureProbe(ReconciliationFailure failure)
        : IWorkloadCoordinatorConformanceProbe
    {
        readonly ConcurrentDictionary<WorkloadIdentity, object> _owners = new();
        int _phase;
        int _workers;

        public TimeSpan ConvergenceTimeout => TimeSpan.FromMilliseconds(500);
        public TimeSpan StabilityWindow => TimeSpan.FromMilliseconds(100);

        public ValueTask ResetAsync(CancellationToken ct = default)
        {
            _owners.Clear();
            _workers = 0;
            _phase++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IWorkloadCoordinatorConformanceWorker> OpenWorkerAsync(CancellationToken ct = default)
        {
            var worker = Interlocked.Increment(ref _workers);
            IWorkloadCoordinator coordinator = _phase == 1
                ? new SharedCoordinator(_owners, false)
                : worker != 1
                    ? new InertCoordinator()
                    : failure == ReconciliationFailure.DropRetained
                        ? new DropsRetainedCoordinator()
                        : new ReacquiresRevokedCoordinator();
            return ValueTask.FromResult<IWorkloadCoordinatorConformanceWorker>(new CoordinatorWorker(coordinator));
        }
    }

    sealed class IgnoringCancellationProbe(bool duplicateOwners = false) : IWorkloadCoordinatorConformanceProbe
    {
        int _workers;
        public TimeSpan ConvergenceTimeout => TimeSpan.FromMilliseconds(500);
        public TimeSpan StabilityWindow => TimeSpan.FromMilliseconds(100);
        public ValueTask ResetAsync(CancellationToken ct = default)
        {
            _workers = 0;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IWorkloadCoordinatorConformanceWorker> OpenWorkerAsync(CancellationToken ct = default)
        {
            if (!duplicateOwners && Interlocked.Increment(ref _workers) != 1)
            {
                return ValueTask.FromResult<IWorkloadCoordinatorConformanceWorker>(
                    new CoordinatorWorker(new InertCoordinator()));
            }

            var coordinator = new IgnoringCancellationCoordinator();
            return ValueTask.FromResult<IWorkloadCoordinatorConformanceWorker>(
                new CoordinatorWorker(coordinator, coordinator.DisposeAsync));
        }
    }

    sealed class FirstOwnerCoordinator : IWorkloadCoordinator
    {
        public async Task RunAsync(Func<IReadOnlyCollection<WorkloadIdentity>> workloads,
            Func<WorkloadIdentity, CancellationToken, Task> run, CancellationToken ct = default)
        {
            var identity = Assert.Single(workloads());
            using var ownership = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var owned = run(identity, ownership.Token);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                await ownership.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owned);
            }
        }
    }

    sealed class InertCoordinator : IWorkloadCoordinator
    {
        public Task RunAsync(Func<IReadOnlyCollection<WorkloadIdentity>> workloads,
            Func<WorkloadIdentity, CancellationToken, Task> run, CancellationToken ct = default) =>
            Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    sealed class DropsRetainedCoordinator : IWorkloadCoordinator
    {
        public async Task RunAsync(Func<IReadOnlyCollection<WorkloadIdentity>> workloads,
            Func<WorkloadIdentity, CancellationToken, Task> run, CancellationToken ct = default)
        {
            var identity = Assert.Single(workloads());
            using var ownership = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var owned = run(identity, ownership.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
            await ownership.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owned);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    sealed class ReacquiresRevokedCoordinator : IWorkloadCoordinator
    {
        public async Task RunAsync(Func<IReadOnlyCollection<WorkloadIdentity>> workloads,
            Func<WorkloadIdentity, CancellationToken, Task> run, CancellationToken ct = default)
        {
            var first = Assert.Single(workloads());
            var owned = new List<(CancellationTokenSource Cancellation, Task Run)>();
            try
            {
                Start(first);
                WorkloadIdentity second;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var current = workloads();
                    if (current.Count == 1 && !current.Contains(first))
                    {
                        second = Assert.Single(current);
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(5), ct);
                }

                await StopAsync(owned[0]);
                Start(second);
                // Reacquire synchronously after replacement starts, so scheduler contention cannot move
                // the injected violation beyond the conformance suite's finite stability window.
                Start(first);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                foreach (var workload in owned.ToArray())
                    await StopAsync(workload);
            }

            void Start(WorkloadIdentity identity)
            {
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                owned.Add((cancellation, run(identity, cancellation.Token)));
            }

            static async Task StopAsync((CancellationTokenSource Cancellation, Task Run) workload)
            {
                if (workload.Cancellation.IsCancellationRequested)
                    return;
                await workload.Cancellation.CancelAsync();
                try
                {
                    await workload.Run;
                }
                catch (OperationCanceledException) when (workload.Cancellation.IsCancellationRequested)
                {
                }
                finally
                {
                    workload.Cancellation.Dispose();
                }
            }
        }
    }

    sealed class IgnoringCancellationCoordinator : IWorkloadCoordinator, IAsyncDisposable
    {
        readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly CancellationTokenSource _lifetime = new();
        Task? _callback;

        public Task RunAsync(Func<IReadOnlyCollection<WorkloadIdentity>> workloads,
            Func<WorkloadIdentity, CancellationToken, Task> run, CancellationToken ct = default)
        {
            _callback = run(Assert.Single(workloads()), _lifetime.Token);
            return _completion.Task;
        }

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            if (_callback is not null)
            {
                try
                {
                    await _callback;
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                }
            }

            _completion.TrySetCanceled(_lifetime.Token);
            _lifetime.Dispose();
        }
    }

    sealed class CoordinatorWorker(
        IWorkloadCoordinator coordinator,
        Func<ValueTask>? dispose = null) : IWorkloadCoordinatorConformanceWorker
    {
        public IWorkloadCoordinator Coordinator { get; } = coordinator;

        public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask;
    }
}
