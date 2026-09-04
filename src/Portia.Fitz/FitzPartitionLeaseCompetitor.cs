using Cntryl.Fitz.Abstractions.Domains.Lease;

namespace Cntryl.Portia;

/// <summary>
/// Adapts a real <see cref="ILeaseClient" /> down to the one operation <see cref="FleetPartitionRunner" />
/// needs, via <see cref="IPartitionLeaseCompetitor" />. Registered automatically by
/// <c>AddPortiaFleetPartitionRunner</c> — an app only ever needs to register <see cref="ILeaseClient" />
/// itself.
/// </summary>
/// <param name="client">The underlying Fitz lease client to compete through.</param>
public sealed class FitzPartitionLeaseCompetitor(ILeaseClient client) : IPartitionLeaseCompetitor
{
    readonly ILeaseClient _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public Task WithLeaseAsync(
        string route,
        ulong ttlSecs,
        Func<LeaseAuthority, CancellationToken, ValueTask> callback,
        LeaseExecutionOptions? options = null,
        CancellationToken ct = default) =>
        _client.WithLeaseAsync(route, ttlSecs, callback, options, ct);
}
