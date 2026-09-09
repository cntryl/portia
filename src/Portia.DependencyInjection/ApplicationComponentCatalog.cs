using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

sealed class ApplicationComponentCatalog
{
    internal Dictionary<string, Action<IServiceCollection>> Workers { get; } = new(StringComparer.Ordinal);
    internal Dictionary<Type, WorkloadRegistration> Workloads { get; } = [];
    internal Dictionary<string, Type> WorkloadNames { get; } = new(StringComparer.Ordinal);
    internal HashSet<Type> ProjectorDescriptors { get; } = [];
    internal HashSet<Type> ReactorDescriptors { get; } = [];
    internal Dictionary<Type, RequestTransportRegistration> Requests { get; } = [];
    internal Dictionary<Type, Type> HandlerRequests { get; } = [];
    internal Dictionary<(Type ScopeType, Type AuthorizerType), AuthorizationStage> Authorizers { get; } = [];
    internal Dictionary<(Type ScopeType, Type BehaviorType), int> Behaviors { get; } = [];
    internal bool WorkersActivated { get; set; }
}

sealed class PortiaJsonComposer
{
    readonly HashSet<Type> _roots = [];
    readonly List<Action<JsonSerializerOptions>> _configuration = [];
    readonly List<Func<JsonSerializerOptions, JsonSerializerContext>> _contexts =
        [static options => new PortiaCoreJsonContext(options)];

    internal void Configure(Action<JsonSerializerOptions> configure) => _configuration.Add(configure);
    internal void AddContext(Func<JsonSerializerOptions, JsonSerializerContext> factory) => _contexts.Add(factory);
    internal void AddRoot(Type type) => _ = _roots.Add(type);

    internal JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        foreach (var configure in _configuration) configure(options);
        foreach (var factory in _contexts) options.TypeInfoResolverChain.Add(factory(new JsonSerializerOptions(options)));
        options.MakeReadOnly();
        var missing = _roots.Where(type => !options.TryGetTypeInfo(type, out _))
            .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        return missing.Length == 0 ? options : throw new InvalidOperationException(
            $"Portia JSON metadata is missing for: {string.Join(", ", missing.Select(type => type.FullName ?? type.Name))}.");
    }
}
