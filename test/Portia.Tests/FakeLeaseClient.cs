using Cntryl.Fitz.Abstractions.Domains.Lease;

namespace Cntryl.Portia;

/// <summary>
/// Stands in for the real Fitz lease broker: exactly one <c>WithLeaseAsync</c> call per route can
/// be "holding" it at a time — a second concurrent call for the same route blocks until the
/// first releases it, modeling real lease contention closely enough to exercise
/// <see cref="FleetPartitionRunner" />'s competition and rebalancing behavior without a live
/// Fitz backend. Only the one <c>WithLeaseAsync</c> overload <see cref="FleetPartitionRunner" />
/// actually calls is implemented; every other <see cref="ILeaseClient" /> member is unused by
/// anything under test and throws if ever called.
/// </summary>
sealed class FakeLeaseClient : ILeaseClient
{
    readonly Dictionary<string, SemaphoreSlim> _locks = [];
    readonly HashSet<string> _acquisitionFailures = [];
    ulong _nextFencingToken;

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
    public void FailNextAcquisition(string route) => _acquisitionFailures.Add(route);

    public async Task WithLeaseAsync(
        string route,
        ulong ttlSecs,
        Func<LeaseAuthority, CancellationToken, ValueTask> callback,
        LeaseExecutionOptions? options = null,
        CancellationToken ct = default)
    {
        var gate = GetGate(route);
        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (_acquisitionFailures.Remove(route))
                throw new InvalidOperationException($"Simulated: '{route}' is still held by another owner.");

            var authority = new LeaseAuthority(++_nextFencingToken);
            lock (Acquisitions)
                Acquisitions.Add(route);

            await callback(authority, ct).ConfigureAwait(false);
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

    public Task<ILease> AcquireAsync(string route, ulong ttlSecs, uint waitSeconds = 0, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<T> WithLeaseAsync<T>(string route, ulong ttlSecs, Func<CancellationToken, ValueTask<T>> callback, LeaseExecutionOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<T> WithLeaseAsync<T>(string route, ulong ttlSecs, Func<LeaseAuthority, CancellationToken, ValueTask<T>> callback, LeaseExecutionOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task WithLeaseAsync(string route, ulong ttlSecs, Func<CancellationToken, ValueTask> callback, LeaseExecutionOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<LeaseInfo> QueryAsync(string route, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<LeaseSubscription> SubscribeAsync(string route, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<LeaseListResult> ListAsync(string pattern, LeaseListCursor? cursor = null, int? limit = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<ILeaseInventoryObserver> ObserveAsync(string pattern, LeaseObserveOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
