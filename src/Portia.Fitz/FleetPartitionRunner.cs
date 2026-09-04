using Cntryl.Fitz.Abstractions.Domains.Lease;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
/// Runs one instance of a per-partition component across a dynamically-sized pool of worker
/// processes, using Fitz leases so exactly one worker holds each partition at a time — the fleet
/// distribution counterpart to <c>MultiTenantRunner</c>. The two are orthogonal and
/// compose freely: <c>MultiTenantRunner</c> decides which tenants a component instance
/// runs for on whichever worker it's already running on; <see cref="FleetPartitionRunner" />
/// decides which worker gets to run a given partition at all. A common shape is a partition per
/// tenant (or per tenant shard), with <c>MultiTenantRunner</c> driving work for the
/// tenants a partition assigns to this worker once <see cref="FleetPartitionRunner" /> has won
/// it.
///
/// The set of partitions is fixed at deployment time (e.g., a known list of shard routes) — only
/// the number of workers competing to run them is dynamic. Rebalancing needs no explicit logic
/// here at all: it falls entirely out of <c>ILeaseClient.WithLeaseAsync</c>'s own behavior —
/// confirmed against a real Fitz broker (see <c>FitzBrokerFleetIntegrationTests</c>), not just
/// inferred from its method shapes. A held lease really is renewed automatically for as long as
/// its callback keeps running, well past its own TTL; and a worker that disappears without
/// releasing gracefully (its connection torn down mid-hold, not a clean shutdown) really does
/// free the lease for an already-waiting worker once the TTL lapses, with no extra coordination
/// needed on either side.
/// </summary>
/// <param name="leases">Competes for each partition's lease — typically a <see cref="FitzPartitionLeaseCompetitor" />
/// wrapping the app's registered <see cref="ILeaseClient" />.</param>
/// <param name="logger">
/// Reports a partition that can't be acquired or whose callback faults even when nothing is
/// listening to <see cref="PortiaTelemetry.ActivitySource" />. Supply it explicitly, or
/// configure Microsoft.Extensions.Logging with at least one provider before resolving the
/// runner through DI; a bare <c>ServiceCollection</c> registration does not create or emit logs.
/// </param>
public sealed class FleetPartitionRunner(IPartitionLeaseCompetitor leases, ILogger<FleetPartitionRunner>? logger = null)
{
    readonly IPartitionLeaseCompetitor _leases = leases ?? throw new ArgumentNullException(nameof(leases));
    readonly ILogger<FleetPartitionRunner>? _logger = logger;

    /// <summary>
    /// Competes for every partition's lease and, while holding one, runs
    /// <paramref name="onPartitionAcquired" /> until either this worker loses that lease (lost
    /// contention, a missed renewal) or <paramref name="ct" /> is cancelled — then competes for
    /// it again, unless the whole run has been cancelled. Every partition is competed for
    /// independently and concurrently; losing one doesn't affect the others.
    /// </summary>
    /// <param name="partitions">The fixed, deployment-time-known set of partition routes.</param>
    /// <param name="onPartitionAcquired">Runs while this worker holds a partition's lease, until
    /// its own token fires — typically a loop driving a runner against that partition's data,
    /// not a single pass. Receives the lease's fencing token so downstream writes can detect and
    /// reject a stale, already-superseded holder.</param>
    /// <param name="leaseTtl">How long a held lease survives without renewal — Fitz renews it
    /// automatically for as long as <paramref name="onPartitionAcquired" /> keeps running.</param>
    /// <param name="ct">A token that can cancel the whole run, across every partition.</param>
    /// <returns>A task representing every partition's competition loop.</returns>
    public Task RunAsync(
        IReadOnlyCollection<string> partitions,
        Func<string, LeaseAuthority, CancellationToken, Task> onPartitionAcquired,
        TimeSpan leaseTtl,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(onPartitionAcquired);

        if (leaseTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseTtl), leaseTtl, "A lease TTL must be positive.");

        var malformed = partitions.FirstOrDefault(partition => !IsWellFormedLeaseRoute(partition));

        if (malformed is not null)
        {
            // A malformed route (found the hard way against a real Fitz broker — the in-memory
            // fake this project's own unit tests use never cared about route shape at all) fails
            // every single acquisition attempt forever. A bare "starts with lease://" prefix
            // check — an earlier, incomplete version of this validation — still let routes with
            // too few/many segments, an empty segment, or a wildcard segment through, none of
            // which a real broker accepts either; every one of those would still hit the exact
            // silent, permanent per-second retry loop this validation exists to prevent. Fail
            // once, at the start, against the complete required shape instead.
            throw new ArgumentException(
                $"Partition '{malformed}' is not a valid Fitz lease route — it must be exactly 'lease://{{realm}}/{{area}}/{{resource}}', with no empty or wildcard ('*') segments.",
                nameof(partitions));
        }

        var duplicate = partitions
            .GroupBy(partition => partition, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            // Two competition loops for the same route, from the same worker, isn't merely
            // wasteful — the second one blocks forever waiting on the first, since nothing ever
            // releases a lease this same process already holds. That's a caller mistake worth
            // failing loudly on, not a silently-stuck task nobody notices.
            throw new ArgumentException(
                $"Partition '{duplicate.Key}' appears more than once.", nameof(partitions));
        }

        var ttlSecs = checked((ulong)leaseTtl.TotalSeconds);

        return Task.WhenAll(partitions.Select(partition => CompeteAsync(partition, onPartitionAcquired, ttlSecs, ct)));
    }

    // Fitz requires a lease route to be the exact shape "lease://{realm}/{area}/{resource}" —
    // three non-empty, non-wildcard segments, no more and no fewer. Confirmed directly against a
    // real broker (it rejects anything else with its own "must be lease://{realm}/{area}/{resource}"
    // error) rather than assumed from documentation.
    const string LeaseScheme = "lease://";

    static bool IsWellFormedLeaseRoute(string route)
    {
        if (!route.StartsWith(LeaseScheme, StringComparison.Ordinal))
            return false;

        var segments = route[LeaseScheme.Length..].Split('/');
        return segments.Length == 3 && segments.All(segment => segment.Length > 0 && !segment.Contains('*'));
    }

    async Task CompeteAsync(
        string partition,
        Func<string, LeaseAuthority, CancellationToken, Task> onPartitionAcquired,
        ulong ttlSecs,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Tracks whether the lease was actually acquired this attempt (the callback ran at
            // all) — Fitz.Abstractions has no exception type of its own that distinguishes
            // "still contested, WaitSeconds elapsed" (routine, expected under a busy fleet) from
            // a genuine failure, so this is the one thing that actually can be known here. It
            // keeps the two from being reported identically as a "fault".
            var acquired = false;

            try
            {
                await _leases.WithLeaseAsync(
                    partition,
                    ttlSecs,
                    (authority, leaseCt) =>
                    {
                        acquired = true;
                        return new ValueTask(onPartitionAcquired(partition, authority, leaseCt));
                    },
                    new LeaseExecutionOptions { WaitForAvailability = true },
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Visible via the same fault-reporting mechanism every other Portia runner uses —
                // a partition that can't be acquired, or whose callback keeps failing, must never
                // just vanish. The reason text differs so the two situations are distinguishable
                // by whatever consumes this telemetry, not just by coincidence of message detail.
                var reason = acquired
                    ? $"partition '{partition}' callback faulted while held"
                    : $"partition '{partition}' could not be acquired";
                PortiaTelemetry.RecordRunnerFault(nameof(FleetPartitionRunner), reason, ex, _logger);
            }

            // Reached whenever WithLeaseAsync returned for any reason other than our own
            // cancellation above — the callback returning on its own, a lost lease, or a caught
            // exception. Always pause before competing again: without this, a callback that
            // returns quickly (by its own design, or because the lease was lost) spins this loop
            // as fast as the lease client allows, hammering it with no backoff at all.
            if (ct.IsCancellationRequested)
                break;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
