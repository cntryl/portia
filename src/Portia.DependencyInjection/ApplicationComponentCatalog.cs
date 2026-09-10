using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

sealed class ApplicationComponentCatalog
{
    internal Dictionary<string, Action<IServiceCollection>> Workers { get; } = new(StringComparer.Ordinal);
    internal Dictionary<Type, WorkloadRegistration> Workloads { get; } = [];
    internal Dictionary<string, Type> WorkloadNames { get; } = new(StringComparer.Ordinal);
    internal HashSet<Type> ComponentDescriptors { get; } = [];
    internal Dictionary<Type, RequestTransportRegistration> Requests { get; } = [];
    internal Dictionary<Type, Type> HandlerRequests { get; } = [];
    internal Dictionary<(Type ScopeType, Type AuthorizerType), AuthorizationStage> Authorizers { get; } = [];
    internal Dictionary<(Type ScopeType, Type BehaviorType), int> Behaviors { get; } = [];
    internal bool WorkersActivated { get; set; }
}
