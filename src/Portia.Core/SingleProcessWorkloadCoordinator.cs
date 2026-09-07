namespace Cntryl.Portia;

/// <summary>
/// Owns every declared workload unconditionally, without leases or competition. This is the
/// default coordinator for a host that runs one worker replica: it lets projectors and reactors
/// registered with <c>AddProjector</c>/<c>AddReactor</c> run with no coordination infrastructure
/// at all.
///
/// It is safe only while exactly one replica is running. A deployment that scales workers beyond
/// one replica must register a distributed <see cref="IWorkloadCoordinator" /> — Fitz supplies one
/// through <c>AddPortiaFitz</c> — because this implementation cannot detect, and will not fence
/// against, a second owner of the same workload.
/// </summary>
public sealed class SingleProcessWorkloadCoordinator : IWorkloadCoordinator
{
    readonly TimeSpan _reconcileInterval;
    readonly TimeProvider _clock;
    ulong _fencingToken;

    /// <summary>Creates a coordinator that reconciles the declared workloads on an interval.</summary>
    /// <param name="reconcileInterval">How long to wait between snapshots of the declared
    /// workloads; defaults to one second. Per-tenant workloads appearing after start are picked up
    /// on the next reconcile.</param>
    /// <param name="timeProvider">The clock used for the reconcile delay, or
    /// <see langword="null" /> for the system clock.</param>
    public SingleProcessWorkloadCoordinator(TimeSpan? reconcileInterval = null, TimeProvider? timeProvider = null)
    {
        _reconcileInterval = reconcileInterval ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_reconcileInterval, TimeSpan.Zero, nameof(reconcileInterval));
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task RunAsync(
        Func<IReadOnlyCollection<WorkloadIdentity>> workloads,
        Func<WorkloadIdentity, ulong, CancellationToken, Task> run,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workloads);
        ArgumentNullException.ThrowIfNull(run);

        var owned = new Dictionary<WorkloadIdentity, OwnedWorkload>();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var declared = workloads()
                    ?? throw new InvalidOperationException("The workload snapshot cannot be null.");
                var current = new HashSet<WorkloadIdentity>(declared);

                // Release ownership of workloads that are no longer declared, awaiting each
                // revoked callback before it stops being owned.
                foreach (var identity in owned.Keys.Where(identity => !current.Contains(identity)).ToArray())
                    await ReleaseAsync(owned, identity).ConfigureAwait(false);

                // A callback that ended on its own either failed unrecoverably, which ends
                // coordination, or completed and is restarted below under a fresh fencing token.
                foreach (var identity in owned.Keys.ToArray())
                {
                    var workload = owned[identity];
                    if (!workload.Run.IsCompleted)
                        continue;
                    await ReleaseAsync(owned, identity).ConfigureAwait(false);
                }

                foreach (var identity in current)
                {
                    if (owned.ContainsKey(identity))
                        continue;
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var fencingToken = Interlocked.Increment(ref _fencingToken);
                    owned[identity] = new OwnedWorkload(cancellation, run(identity, fencingToken, cancellation.Token));
                }

                await Task.Delay(_reconcileInterval, _clock, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var identity in owned.Keys.ToArray())
            {
                try
                {
                    await ReleaseAsync(owned, identity).ConfigureAwait(false);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    // Shutdown must release every remaining workload, not stop at the first fault.
                }
            }
        }
    }

    static async Task ReleaseAsync(Dictionary<WorkloadIdentity, OwnedWorkload> owned, WorkloadIdentity identity)
    {
        var workload = owned[identity];
        _ = owned.Remove(identity);
        await workload.Cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await workload.Run.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (workload.Cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            workload.Cancellation.Dispose();
        }
    }

    sealed record OwnedWorkload(CancellationTokenSource Cancellation, Task Run);
}
