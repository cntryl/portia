
namespace Cntryl.Portia;

/// <summary>Holds a renewable worker lease and owns its fleet inventory observer for that lease's lifetime.</summary>
/// <param name="client">The concrete application's Fitz lease client.</param>
public sealed class FitzFleetMembership(ILeaseClient client) : IFleetMembership
{
    readonly ILeaseClient _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public Task RunAsync(FleetRunOptions options, Func<ILeaseInventoryObserver, CancellationToken, Task> callback,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(callback);
        options.Validate([]);
        var workerId = options.WorkerId ?? Guid.NewGuid().ToString("D");
        return _client.WithLeaseAsync(options.MembershipSelector[..^1] + workerId,
            TimeSpan.FromSeconds(options.TtlSeconds),
            async (_, membershipCt) =>
            {
                await using var observer = await _client.ObserveAsync(options.MembershipSelector,
                        new LeaseObserveOptions { ReconciliationInterval = options.ReconciliationInterval },
                        membershipCt)
                    .ConfigureAwait(false);
                await callback(observer, membershipCt).ConfigureAwait(false);
            }, new LeaseExecutionOptions(), ct);
    }
}
