using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

sealed class FitzWorkloadCoordinator(FitzApplicationConnection connection, PortiaFitzBuilder configuration,
    ILogger<FleetPartitionRunner>? logger, TimeProvider? clock) : IWorkloadCoordinator
{
    static readonly Uuid NamespaceId = Uuid.Parse("c41f6c39-9c25-4f7f-b8b0-fce08b5ccfc4");

    public async Task RunAsync(Func<IReadOnlyCollection<WorkloadIdentity>> workloads,
        Func<WorkloadIdentity, ulong, CancellationToken, Task> run, CancellationToken ct = default)
    {
        await connection.StartAsync(ct).ConfigureAwait(false);
        var options = configuration.Fleet ?? new FleetRunOptions { MembershipSelector = "lease://portia/portia-members/*" };
        var membership = options.MembershipSelector[8..].Split('/');
        var prefix = $"lease://{membership[0]}/{membership[1]}-workloads/";
        IReadOnlyDictionary<string, WorkloadIdentity> current = new Dictionary<string, WorkloadIdentity>();
        IReadOnlyCollection<string> Snapshot()
        {
            var snapshot = workloads().ToDictionary(identity => prefix + Uuid.CreateVersion5(NamespaceId,
                JsonSerializer.Serialize(new[] { identity.Name, identity.Tenant?.Value })), identity => identity);
            Volatile.Write(ref current, snapshot);
            return [.. snapshot.Keys];
        }
        var fleet = new FleetPartitionRunner(new FitzPartitionLeaseCompetitor(connection.Client.Lease),
            new FitzFleetMembership(connection.Client.Lease), logger, clock);
        await fleet.RunAsync(Snapshot, (route, authority, token) =>
            Volatile.Read(ref current).TryGetValue(route, out var identity)
                ? run(identity, authority.FencingToken, token) : Task.CompletedTask, options, ct).ConfigureAwait(false);
    }
}
