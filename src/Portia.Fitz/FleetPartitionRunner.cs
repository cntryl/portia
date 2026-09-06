using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Cntryl.Fitz.Abstractions.Domains.Lease;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>Assigns fixed partitions by rendezvous hashing over renewable fleet membership, then acquires each assigned lease.</summary>
/// <param name="leases">Acquires partition fencing authority.</param>
/// <param name="membership">Owns this worker's renewable membership and inventory.</param>
/// <param name="logger">Reports membership and partition failures and assignment changes.</param>
/// <param name="timeProvider">Schedules reconciliation and cancellable retry backoff.</param>
public sealed class FleetPartitionRunner(IPartitionLeaseCompetitor leases, IFleetMembership membership,
    ILogger<FleetPartitionRunner>? logger = null, TimeProvider? timeProvider = null)
{
    readonly IPartitionLeaseCompetitor _leases = leases ?? throw new ArgumentNullException(nameof(leases));
    readonly IFleetMembership _membership = membership ?? throw new ArgumentNullException(nameof(membership));
    readonly ILogger<FleetPartitionRunner>? _logger = logger;
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>Runs assigned partitions until cancellation. Every fleet worker must use the same selector, partitions, and algorithm.</summary>
    /// <param name="partitions">The fixed set of exact lease routes outside the membership area.</param>
    /// <param name="onPartitionAcquired">Runs under a lease's fencing authority and cancellation token.</param>
    /// <param name="options">Membership, timing, and optional stable worker identity.</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>The complete lifetime, including observation of all revoked work.</returns>
    public Task RunAsync(IReadOnlyCollection<string> partitions,
        Func<string, LeaseAuthority, CancellationToken, Task> onPartitionAcquired,
        FleetRunOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onPartitionAcquired);
        options.Validate(partitions);
        var runOptions = options with { WorkerId = options.WorkerId ?? Guid.NewGuid().ToString("D") };
        return RunMembershipAsync(() => partitions, onPartitionAcquired, runOptions, ct);
    }

    /// <summary>Reconciles a changing partition snapshot without restarting retained assignments.</summary>
    public Task RunAsync(Func<IReadOnlyCollection<string>> partitions,
        Func<string, LeaseAuthority, CancellationToken, Task> onPartitionAcquired,
        FleetRunOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(onPartitionAcquired);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(partitions());
        return RunMembershipAsync(partitions, onPartitionAcquired,
            options with { WorkerId = options.WorkerId ?? Guid.NewGuid().ToString("D") }, ct);
    }

    async Task RunMembershipAsync(Func<IReadOnlyCollection<string>> partitions, Func<string, LeaseAuthority, CancellationToken, Task> callback,
        FleetRunOptions options, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _membership.RunAsync(options, (observer, membershipCt) =>
                    ReconcileAsync(partitions, callback, options, observer, membershipCt), ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                    Fault("membership ended without cancellation");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Fault("membership failed", ex); }
            if (!await BackoffAsync(ct).ConfigureAwait(false))
                break;
        }
    }

    async Task ReconcileAsync(Func<IReadOnlyCollection<string>> partitions, Func<string, LeaseAuthority, CancellationToken, Task> callback,
        FleetRunOptions options, ILeaseInventoryObserver observer, CancellationToken ct)
    {
        var active = new Dictionary<string, PartitionRun>(StringComparer.Ordinal);
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
                    Fault("membership snapshot is not ready or does not contain this worker");
                unavailable = !ready;
                var current = partitions();
                options.Validate(current);
                var assigned = ready
                    ? current.Where(partition => GetOwner(partition, workers) == options.WorkerId).ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                var revoked = active.Keys.Where(partition => !assigned.Contains(partition)).ToArray();
                foreach (var partition in revoked)
                    Cancel(partition, active[partition].Cancellation);
                foreach (var partition in revoked)
                {
                    await ObserveAsync(partition, active[partition]).ConfigureAwait(false);
                    _ = active.Remove(partition);
                    PortiaTelemetry.RecordFleetAssignment(options.WorkerId!, partition, false, _logger);
                }
                ct.ThrowIfCancellationRequested();
                foreach (var partition in assigned)
                {
                    if (active.ContainsKey(partition))
                        continue;
                    ct.ThrowIfCancellationRequested();
                    var cancellation = new CancellationTokenSource();
                    var registration = ct.Register(() => Cancel(partition, cancellation));
                    active.Add(partition, new PartitionRun(cancellation,
                        Task.Run(() => CompeteAsync(partition, callback, options.TtlSeconds, cancellation.Token), CancellationToken.None), registration));
                    PortiaTelemetry.RecordFleetAssignment(options.WorkerId!, partition, true, _logger);
                }
                await Task.Delay(options.ReconciliationInterval, _clock, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var (partition, run) in active)
                Cancel(partition, run.Cancellation);
            foreach (var (partition, run) in active)
                await ObserveAsync(partition, run).ConfigureAwait(false);
        }
    }

    // SHA-256 of [uint32 BE byte length][UTF-8 route][uint32 BE byte length][UTF-8 worker ID].
    // Compare unsigned digests lexicographically; greatest ordinal worker ID wins digest ties.
    static string? GetOwner(string partition, string[] workers)
    {
        string? winner = null;
        byte[]? greatest = null;
        var route = Encoding.UTF8.GetBytes(partition);
        foreach (var worker in workers)
        {
            var id = Encoding.UTF8.GetBytes(worker);
            var input = new byte[8 + route.Length + id.Length];
            BinaryPrimitives.WriteInt32BigEndian(input, route.Length);
            route.CopyTo(input, 4);
            BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(4 + route.Length), id.Length);
            id.CopyTo(input, 8 + route.Length);
            var digest = SHA256.HashData(input);
            var comparison = greatest is null ? 1 : digest.AsSpan().SequenceCompareTo(greatest);
            if (comparison > 0 || (comparison == 0 && string.CompareOrdinal(worker, winner) > 0))
            {
                greatest = digest;
                winner = worker;
            }
        }
        return winner;
    }

    async Task CompeteAsync(string partition, Func<string, LeaseAuthority, CancellationToken, Task> callback,
        ulong ttl, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var acquired = false;
            try
            {
                await _leases.WithLeaseAsync(partition, ttl, (authority, leaseCt) =>
                {
                    acquired = true;
                    return new ValueTask(callback(partition, authority, leaseCt));
                }, new LeaseExecutionOptions { WaitForAvailability = true }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Fault(acquired ? $"partition '{partition}' callback faulted while held" : $"partition '{partition}' could not be acquired", ex);
            }
            if (!await BackoffAsync(ct).ConfigureAwait(false))
                break;
        }
    }

    async Task<bool> BackoffAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(1), _clock, ct).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
    }

    void Cancel(string partition, CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); }
        catch (Exception ex) { Fault($"partition '{partition}' cancellation failed", ex); }
    }

    async Task ObserveAsync(string partition, PartitionRun run)
    {
        try { await run.Task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested) { }
        catch (Exception ex) { Fault($"partition '{partition}' termination failed", ex); }
        finally { run.Registration.Dispose(); run.Cancellation.Dispose(); }
    }

    void Fault(string reason, Exception? exception = null) =>
        PortiaTelemetry.RecordRunnerFault(nameof(FleetPartitionRunner), reason, exception, _logger);

    sealed record PartitionRun(CancellationTokenSource Cancellation, Task Task, CancellationTokenRegistration Registration);
}
