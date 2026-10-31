using System.Reflection;

namespace Cntryl.Portia;

sealed class GeneratedApplicationTopology(IEnumerable<RequestHandlerRegistration> requests,
    IEnumerable<WorkloadRegistration> workloads, IEnumerable<RequestTransportRegistration> transports) : IApplicationTopology
{
    public ApplicationTopology Describe() => new(
        DescribeRequests(),
        workloads.Select(w =>
        {
            var generated = w.ComponentType.GetCustomAttribute<ProcessorTopologyAttribute>();
            return new ProcessorTopology(w.Name, w.ComponentType.FullName ?? w.ComponentType.Name,
                w.Descriptor.SupportsRebuild ? "projector" : "reactor", w.Scope.ToString(),
                generated?.Subscriptions.ToArray() ?? [], generated is null
                    ? "unknown: manual dispatch" : "generated dispatch; overrides and opaque dependencies unknown");
        }).OrderBy(w => w.Name, StringComparer.Ordinal).ToArray(),
        "selected registrations only; opaque dependencies and remote effects unknown");

    RequestTopology[] DescribeRequests()
    {
        var handlers = requests.ToDictionary(r => r.RequestType, r => r.HandlerType);
        return handlers.Keys.Concat(transports.Select(r => r.RequestType)).Distinct()
            .Select(type => new RequestTopology(type.FullName ?? type.Name,
                handlers.TryGetValue(type, out var handler) ? handler.FullName ?? handler.Name : "unknown: remote or no registered handler"))
            .OrderBy(r => r.Request, StringComparer.Ordinal).ToArray();
    }
}
