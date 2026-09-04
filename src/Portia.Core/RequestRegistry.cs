namespace Cntryl.Portia;

/// <summary>Composes generated descriptors without resolving any application components.</summary>
public sealed class RequestRegistry
{
    readonly Dictionary<Type, RequestHandlerRegistration> _handlers = [];
    readonly Dictionary<Type, RequestAuthorizerRegistration> _authorizers = [];

    /// <summary>Creates the registry and rejects conflicting registrations.</summary>
    /// <param name="handlers">Generated handler descriptors.</param>
    /// <param name="authorizers">Generated authorizer descriptors.</param>
    public RequestRegistry(IEnumerable<RequestHandlerRegistration> handlers, IEnumerable<RequestAuthorizerRegistration> authorizers)
    {
        foreach (var registration in handlers)
        {
            if (_handlers.TryGetValue(registration.RequestType, out var existing) && existing.HandlerType != registration.HandlerType)
                throw new InvalidOperationException($"Request '{registration.RequestType}' has conflicting handlers '{existing.HandlerType}' and '{registration.HandlerType}'.");
            _handlers[registration.RequestType] = registration;
        }
        foreach (var registration in authorizers)
        {
            if (_authorizers.TryGetValue(registration.RequestType, out var existing) && existing.AuthorizerType != registration.AuthorizerType)
                throw new InvalidOperationException($"Request '{registration.RequestType}' has conflicting authorizers '{existing.AuthorizerType}' and '{registration.AuthorizerType}'.");
            _authorizers[registration.RequestType] = registration;
        }
    }

    internal RequestHandlerRegistration Handler(Type requestType) => _handlers.TryGetValue(requestType, out var registration)
        ? registration : throw new InvalidOperationException($"No handler is registered for request type '{requestType}'.");

    internal RequestAuthorizerRegistration? Authorizer(Type requestType) => _authorizers.GetValueOrDefault(requestType);
}
