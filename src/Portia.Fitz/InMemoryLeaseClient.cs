using Cntryl.Fitz.Abstractions.Domains.Lease;

namespace Cntryl.Portia;

/// <summary>
/// Stands in for a real Fitz lease broker for tests: exactly one <c>WithLeaseAsync</c> call per
/// route can be "holding" it at a time — a second concurrent call for the same route blocks
/// until the first releases it, modeling real lease contention closely enough to exercise
/// <see cref="FleetPartitionRunner" />'s competition and rebalancing behavior with no live Fitz
/// backend. Shipped from <c>Portia.Fitz</c> (not <c>Portia.Testing</c>) since it depends on
/// <c>Cntryl.Fitz.Abstractions</c> — an app testing its own lease-based code can reuse this exact
/// type instead of writing its own fake.
///
/// Implements <see cref="IPartitionLeaseCompetitor" /> — the narrow interface
/// <see cref="FleetPartitionRunner" /> actually depends on — rather than the full
/// <see cref="ILeaseClient" />, so this is an honest, fully-substitutable stand-in with no
/// throwing "not supported" members.
/// </summary>
public sealed class InMemoryLeaseClient : IPartitionLeaseCompetitor
{
    readonly Dictionary<string, SemaphoreSlim> _locks = [];
    readonly HashSet<string> _acquisitionFailures = [];

    /// <summary>
    /// Gets, in order, every route that successfully acquired its lease.
    /// </summary>
    public List<string> Acquisitions { get; } = [];

    /// <summary>
    /// Makes the next <c>WithLeaseAsync</c> call for <paramref name="route" /> throw before ever
    /// invoking its callback — simulating real contention (the wait for availability elapsed
    /// while another holder still had it) rather than a callback that ran and then failed on
    /// its own.
    /// </summary>
    public void FailNextAcquisition(string route)
    {
        lock (_locks)
            _ = _acquisitionFailures.Add(route);
    }

    /// <inheritdoc />
    public async Task WithLeaseAsync(
        string route,
        ulong ttlSecs,
        Func<CancellationToken, ValueTask> callback,
        LeaseExecutionOptions? options = null,
        CancellationToken ct = default)
    {
        var gate = GetGate(route);
        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            lock (_locks)
            {
                if (_acquisitionFailures.Remove(route))
                    throw new InvalidOperationException($"Simulated: '{route}' is still held by another owner.");
            }
            lock (Acquisitions)
                Acquisitions.Add(route);

            await callback(ct).ConfigureAwait(false);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    SemaphoreSlim GetGate(string route)
    {
        lock (_locks)
        {
            if (!_locks.TryGetValue(route, out var gate))
                _locks[route] = gate = new SemaphoreSlim(1, 1);
            return gate;
        }
    }
}
