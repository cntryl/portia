using Cntryl.Fitz.Abstractions.Domains.Lease;

namespace Cntryl.Portia;

sealed class SingleWorkerMembership : IFleetMembership
{
    public static FleetRunOptions Options(TimeSpan ttl) => new() { MembershipSelector = "lease://portia/members/*", LeaseTtl = ttl };
    public Task RunAsync(FleetRunOptions options, Func<ILeaseInventoryObserver, CancellationToken, Task> callback, CancellationToken ct = default)
        => callback(new Observer(options), ct);
    sealed class Observer(FleetRunOptions options) : ILeaseInventoryObserver
    {
        public bool IsReady => true;
        public IReadOnlyDictionary<string, LeaseListItem> View { get; } = new Dictionary<string, LeaseListItem>
        {
            [options.MembershipSelector[..^1] + options.WorkerId] = new(options.MembershipSelector[..^1] + options.WorkerId, "owner", 1, "", 30, 0),
        };
        public IAsyncEnumerable<LeaseInventoryUpdate> Updates => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
