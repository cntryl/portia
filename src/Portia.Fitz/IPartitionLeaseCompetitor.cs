using Cntryl.Fitz.Abstractions.Domains.Lease;

namespace Cntryl.Portia;

/// <summary>
/// The one lease-competition operation Portia's fleet distribution actually needs from a Fitz
/// lease client: compete for a route's lease and run a callback for as long as this worker holds
/// it, stopping when the callback returns, the lease is lost, or <see cref="CancellationToken" />
/// fires. Deliberately narrower than the full <see cref="ILeaseClient" /> — <see cref="FleetPartitionRunner" />
/// is this interface's only consumer, and it never needs <see cref="ILeaseClient" />'s
/// query/subscribe/list/observe/acquire-without-callback members. Depending on this instead of
/// <see cref="ILeaseClient" /> directly means a test double only has to implement the one
/// operation actually used to be a fully honest, substitutable stand-in — see
/// <see cref="InMemoryLeaseClient" />.
/// </summary>
public interface IPartitionLeaseCompetitor
{
    /// <summary>
    /// Competes for <paramref name="route" />'s lease and, once acquired, runs
    /// <paramref name="callback" /> until it returns, the lease is lost, or
    /// <paramref name="ct" /> is cancelled.
    /// </summary>
    Task WithLeaseAsync(
        string route,
        ulong ttlSecs,
        Func<LeaseAuthority, CancellationToken, ValueTask> callback,
        LeaseExecutionOptions? options = null,
        CancellationToken ct = default);
}
