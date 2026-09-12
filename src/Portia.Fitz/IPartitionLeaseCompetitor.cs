
namespace Cntryl.Portia;

/// <summary>
///     The one lease-competition operation Portia's fleet distribution actually needs from a Fitz
///     lease client: compete for a route's lease and run a callback for as long as this worker holds
///     it, stopping when the callback returns, the lease is lost, or <see cref="CancellationToken" />
///     fires. Deliberately narrower than the full <see cref="ILeaseClient" /> — <see cref="FleetPartitionRunner" />
///     is this interface's only consumer, and it never needs <see cref="ILeaseClient" />'s
///     query/subscribe/list/observe/acquire-without-callback members. Depending on this instead of
///     <see cref="ILeaseClient" /> directly means a test double only has to implement the one
///     operation actually used to be a fully honest, substitutable stand-in — see
///     <see cref="Testing.InMemoryLeaseClient" />.
/// </summary>
public interface IPartitionLeaseCompetitor
{
    /// <summary>
    ///     Competes for <paramref name="route" />'s lease and, once acquired, runs
    ///     <paramref name="callback" /> until it returns, the lease is lost, or
    ///     <paramref name="ct" /> is cancelled.
    /// </summary>
    /// <param name="route">The lease route to compete for.</param>
    /// <param name="ttlSecs">The lease time-to-live, in seconds.</param>
    /// <param name="callback">Runs for as long as this worker holds the lease.</param>
    /// <param name="options">The lease execution settings, or <see langword="null" /> for the defaults.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the lease-scoped run.</returns>
    Task WithLeaseAsync(
        string route,
        ulong ttlSecs,
        Func<CancellationToken, ValueTask> callback,
        LeaseExecutionOptions? options = null,
        CancellationToken ct = default);
}
