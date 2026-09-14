
namespace Cntryl.Portia;

/// <summary>
///     Adapts a real <see cref="ILeaseClient" /> down to the one operation <see cref="FleetPartitionRunner" />
///     needs, via <see cref="IPartitionLeaseCompetitor" />.
/// </summary>
/// <param name="client">The underlying Fitz lease client to compete through.</param>
public sealed class FitzPartitionLeaseCompetitor(ILeaseClient client) : IPartitionLeaseCompetitor
{
    readonly ILeaseClient _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public Task WithLeaseAsync(
        string route,
        ulong ttlSecs,
        Func<CancellationToken, ValueTask> callback,
        LeaseExecutionOptions? options = null,
        CancellationToken ct = default) =>
        _client.WithLeaseAsync(route, TimeSpan.FromSeconds(ttlSecs), callback, options, ct);
}
