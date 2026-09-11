using Cntryl.Fitz.Abstractions.Domains.Lease;

namespace Cntryl.Portia;

/// <summary>A competitor whose every acquisition fails, standing in for an unreachable broker.</summary>
sealed class AlwaysFailingLeaseCompetitor : IPartitionLeaseCompetitor
{
    public Task WithLeaseAsync(string route, ulong ttlSecs, Func<CancellationToken, ValueTask> callback,
        LeaseExecutionOptions? options = null, CancellationToken ct = default) =>
        throw new InvalidOperationException($"Simulated: the lease broker is unreachable for '{route}'.");
}
