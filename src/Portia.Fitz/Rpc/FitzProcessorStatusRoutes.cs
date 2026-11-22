using System.Globalization;
using System.Text.Json;

namespace Cntryl.Portia;

static class FitzProcessorStatusRoutes
{
    static readonly Uuid NamespaceId = Uuid.Parse("0733e2d0-cd45-4fea-b671-c12f9e06283a", CultureInfo.InvariantCulture);

    internal static RequestRouteValues Values(string application, WorkloadIdentity workload) => new(
        Realm: "portia-processors",
        Area: Uuid.CreateVersion5(NamespaceId, application).ToString(),
        Resource: Uuid.CreateVersion5(NamespaceId,
            JsonSerializer.Serialize(new[] { workload.Name, workload.Tenant?.Value },
                FitzJsonContext.Default.StringArray)).ToString());

    internal static RequestRouteValues ReaderValues(string application) =>
        new("portia-processors", Uuid.CreateVersion5(NamespaceId, application).ToString(), "reader");

    internal static string ReaderRoute(string application) => Route(ReaderValues(application));

    internal static string Route(string application, WorkloadIdentity workload)
        => Route(Values(application, workload));

    static string Route(RequestRouteValues values)
        => $"rpc://{values.Realm}/{values.Area}/{values.Resource}/status";
}
