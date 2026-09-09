namespace Cntryl.Portia;

/// <summary>Composes generated descriptors without resolving any application components.</summary>
public sealed class RequestRegistry
{
    readonly Dictionary<Type, RequestHandlerRegistration> _handlers = [];
    readonly RequestAuthorizerRegistration[] _authorizers;
    readonly RequestPipelineBehaviorRegistration[] _behaviors;

    /// <summary>Creates the registry and rejects conflicting registrations.</summary>
    /// <param name="handlers">Generated handler descriptors.</param>
    /// <param name="authorizers">Generated authorizer descriptors.</param>
    /// <param name="behaviors">Generated ordered pipeline behavior descriptors.</param>
    public RequestRegistry(IEnumerable<RequestHandlerRegistration> handlers, IEnumerable<RequestAuthorizerRegistration> authorizers,
        IEnumerable<RequestPipelineBehaviorRegistration> behaviors)
    {
        foreach (var registration in handlers)
        {
            if (_handlers.TryGetValue(registration.RequestType, out var existing) && existing.HandlerType != registration.HandlerType)
                throw new InvalidOperationException($"Request '{registration.RequestType}' has conflicting handlers '{existing.HandlerType}' and '{registration.HandlerType}'.");
            _handlers[registration.RequestType] = registration;
        }
        var policies = authorizers.ToArray();
        if (policies.FirstOrDefault(registration => !Enum.IsDefined(registration.Stage)) is { } invalid)
            throw new ArgumentOutOfRangeException(nameof(authorizers), invalid.Stage, "Choose a defined authorization stage.");
        foreach (var group in policies.GroupBy(registration => (registration.ScopeType, registration.AuthorizerType)))
        {
            if (group.Select(registration => registration.Stage).Distinct().Skip(1).Any())
                throw new InvalidOperationException($"Authorizer '{group.Key.AuthorizerType}' has conflicting stages.");
        }
        _authorizers = [.. policies
            .DistinctBy(registration => (registration.ScopeType, registration.AuthorizerType))
            .OrderBy(registration => registration.Stage)];
        var extensions = behaviors.ToArray();
        foreach (var group in extensions.GroupBy(registration => (registration.ScopeType, registration.BehaviorType)))
        {
            if (group.Select(registration => registration.Order).Distinct().Skip(1).Any())
                throw new InvalidOperationException($"Pipeline behavior '{group.Key.BehaviorType}' has conflicting orders.");
        }
        _behaviors = [.. extensions.DistinctBy(registration => (registration.ScopeType, registration.BehaviorType))
            .OrderBy(registration => registration.Order)];
    }

    internal RequestHandlerRegistration Handler(Type requestType) => _handlers.TryGetValue(requestType, out var registration)
        ? registration : throw new InvalidOperationException($"No handler is registered for request type '{requestType}'.");

    internal IEnumerable<RequestAuthorizerRegistration> Authorizers(Type requestType) =>
        _authorizers.Where(registration => registration.ScopeType.IsAssignableFrom(requestType));

    internal RequestPipelineBehaviorRegistration[] Behaviors(Type requestType) =>
        [.. _behaviors.Where(registration => registration.ScopeType.IsAssignableFrom(requestType))];
}
