using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia;

/// <summary>Composes shared application services and explicitly activated worker services.</summary>
public sealed class PortiaBuilder
{
    readonly Dictionary<string, Action<IServiceCollection>> _workers = new(StringComparer.Ordinal);
    readonly Dictionary<Type, (WorkloadRegistration Registration, ServiceDescriptor Descriptor)> _workloads = [];
    readonly Dictionary<string, Type> _workloadNames = new(StringComparer.Ordinal);
    readonly HashSet<Type> _projectorDescriptors = [];
    readonly HashSet<Type> _reactorDescriptors = [];
    readonly HashSet<Type> _requests = [];
    readonly Dictionary<Type, Type> _handlerRequests = [];
    readonly Dictionary<(Type ScopeType, Type AuthorizerType), AuthorizationStage> _authorizers = [];
    bool _worker;

    internal PortiaBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>Gets the application's service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Registers a request handler through Portia.Generators' compile-time typed descriptor.</summary>
    /// <remarks>This call requires Portia.Generators in the calling project.</remarks>
    public PortiaBuilder AddRequestHandler<THandler>() where THandler : class
    {
        _ = Services;
        throw MissingGeneratedRegistration(typeof(THandler), "request handler");
    }

    /// <summary>Registers a request authorizer through Portia.Generators' compile-time typed descriptor.</summary>
    /// <remarks>This call requires Portia.Generators in the calling project.</remarks>
    public PortiaBuilder AddRequestAuthorizer<TAuthorizer>(AuthorizationStage stage = AuthorizationStage.ResourceAccess)
        where TAuthorizer : class
    {
        _ = Services;
        throw MissingGeneratedRegistration(typeof(TAuthorizer), $"request authorizer at stage '{stage}'");
    }

    /// <summary>Explicitly registers a request whose concrete type is hidden from compile-time dispatch analysis.</summary>
    /// <remarks>Normal strongly typed dispatch is inferred by Portia.Generators. Use this only at dynamic dispatch boundaries.</remarks>
    public PortiaBuilder RegisterDynamicRequest<TRequest>() where TRequest : IRequestBase
    {
        _ = Services;
        throw MissingGeneratedRegistration(typeof(TRequest), "request");
    }

    static InvalidOperationException MissingGeneratedRegistration(Type type, string role) => new(
        $"Portia.Generators did not intercept registration of {role} '{type}'. Reference Portia.Generators in the calling project.");

    /// <summary>Includes an event type in the application's serializer catalog.</summary>
    public PortiaBuilder AddEvent<TEvent>() where TEvent : DomainEvent
    {
        Services.AddPortiaEvent<TEvent>();
        return this;
    }

    /// <summary>
    /// Adds a handler descriptor built by Portia.Generators at compile time. Application code
    /// calls <see cref="AddRequestHandler{THandler}" /> instead of this method.
    /// </summary>
    /// <param name="registration">The generated descriptor.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaBuilder AddGeneratedHandler(RequestHandlerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (_handlerRequests.TryGetValue(registration.RequestType, out var owner))
        {
            // One handler can serve several requests, so the same registration arriving twice is
            // an idempotent repeat; a different handler for the same request is a conflict.
            return owner == registration.HandlerType ? this
                : throw new InvalidOperationException($"Request '{registration.RequestType}' has conflicting handlers.");
        }
        _handlerRequests[registration.RequestType] = registration.HandlerType;
        _ = Services.AddSingleton(registration);
        Services.TryAddScoped(registration.HandlerType);
        return this;
    }

    /// <summary>
    /// Adds an authorizer descriptor built by Portia.Generators at compile time. Application code
    /// calls <see cref="AddRequestAuthorizer{TAuthorizer}(AuthorizationStage)" /> instead.
    /// </summary>
    /// <param name="registration">The generated descriptor.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaBuilder AddGeneratedAuthorizer(RequestAuthorizerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!Enum.IsDefined(registration.Stage))
            throw new ArgumentOutOfRangeException(nameof(registration), registration.Stage, "Choose a defined authorization stage.");
        var key = (registration.ScopeType, registration.AuthorizerType);
        if (_authorizers.TryGetValue(key, out var stage))
        {
            return stage == registration.Stage ? this
                : throw new InvalidOperationException($"Authorizer '{registration.AuthorizerType}' has conflicting stages.");
        }

        _authorizers[key] = registration.Stage;
        _ = Services.AddSingleton(registration);
        Services.TryAddScoped(registration.AuthorizerType);
        return this;
    }

    /// <summary>
    /// Adds a request's transport metadata, built by Portia.Generators at compile time.
    /// Normal application code does not call this method: Portia.Generators emits it for typed
    /// dispatches, handlers, and <see cref="RegisterDynamicRequest{TRequest}" /> escape hatches.
    /// </summary>
    /// <param name="registration">The generated descriptor.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaBuilder AddGeneratedRequest(RequestTransportRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (_requests.Add(registration.RequestType))
            _ = Services.AddSingleton(registration);
        return this;
    }

    /// <summary>Registers one reactor with an explicitly selected execution scope.</summary>
    public PortiaBuilder AddReactor<TReactor>(Action<WorkloadOptions> configure) where TReactor : BaseReactor
        => AddWorkload(new WorkloadRegistration(typeof(TReactor), false, configure));

    /// <summary>Registers one projector with an explicitly selected execution scope.</summary>
    public PortiaBuilder AddProjector<TProjector>(Action<WorkloadOptions> configure) where TProjector : BaseProjector
        => AddWorkload(new WorkloadRegistration(typeof(TProjector), true, configure), ProjectorRegistration.Create<TProjector>());

    PortiaBuilder AddWorkload(WorkloadRegistration registration, ProjectorRegistration? descriptor = null)
    {
        if (registration.ComponentType.IsAbstract || registration.ComponentType.ContainsGenericParameters)
            throw new ArgumentException("Register a concrete, closed component type.", nameof(registration));
        if (_workloads.TryGetValue(registration.ComponentType, out var existing))
        {
            return existing.Registration == registration ? this
                : throw new InvalidOperationException($"Conflicting workload registration '{registration.Name}'.");
        }
        if (_workloadNames.TryGetValue(registration.Name, out var owner) && owner != registration.ComponentType)
            throw new InvalidOperationException($"Conflicting workload registration '{registration.Name}'.");
        Services.TryAddScoped(registration.ComponentType);
        // The descriptor may already have been registered by hand, so a miss in the builder's own
        // set still has to check the collection — once per component, not once per call.
        if (descriptor is not null)
        {
            if (_projectorDescriptors.Add(registration.ComponentType)
                && !Services.Select(service => service.ImplementationInstance).OfType<ProjectorRegistration>()
                    .Any(item => item.ProjectorType == registration.ComponentType))
            {
                _ = Services.AddSingleton(descriptor);
            }
        }
        else if (_reactorDescriptors.Add(registration.ComponentType)
            && !Services.Select(service => service.ImplementationInstance).OfType<ReactorRegistration>()
                .Any(item => item.ReactorType == registration.ComponentType))
        {
            _ = Services.AddSingleton(new ReactorRegistration(registration.ComponentType,
                provider => (BaseReactor)provider.GetRequiredService(registration.ComponentType)));
        }
        var workloadDescriptor = ServiceDescriptor.Singleton(registration);
        Services.Add(workloadDescriptor);
        _workloads[registration.ComponentType] = (registration, workloadDescriptor);
        _workloadNames[registration.Name] = registration.ComponentType;
        Services.TryAddScoped<ProjectorRunner>();
        Services.TryAddScoped<ReactorRunner>();
        return this;
    }

    /// <summary>Declares named worker-only registrations in shared application setup.</summary>
    public PortiaBuilder ConfigureWorker(string name, Action<IServiceCollection> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        if (_workers.TryGetValue(name, out var existing))
        {
            return existing != configure ? throw new InvalidOperationException($"Worker '{name}' has conflicting registrations.") : this;
        }
        if (_worker)
            configure(Services);
        _workers.Add(name, configure);
        return this;
    }

    /// <summary>Activates the shared application's worker registrations once in this host.</summary>
    public PortiaBuilder AddWorker()
    {
        if (_worker)
            return this;
        var count = Services.Count;
        try
        {
            foreach (var configure in _workers.Values)
                configure(Services);
            _ = Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService, PortiaWorkloadService>();
            _worker = true;
        }
        catch
        {
            while (Services.Count > count)
                Services.RemoveAt(Services.Count - 1);
            throw;
        }
        return this;
    }
}

/// <summary>Registers shared Portia application setup in the standard DI container.</summary>
public static class PortiaApplicationServiceCollectionExtensions
{
    /// <summary>Registers a Portia application whose outbound requests are inferred from typed dispatch calls.</summary>
    public static PortiaBuilder AddPortia(this IServiceCollection services) => AddPortia(services, static _ => { });

    /// <summary>Configures the application's components and capabilities without activating workers.</summary>
    public static PortiaBuilder AddPortia(this IServiceCollection services, Action<PortiaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = services.Select(service => service.ImplementationInstance).OfType<PortiaBuilder>().SingleOrDefault();
        if (builder is null)
        {
            builder = new PortiaBuilder(services);
            _ = services.AddSingleton(builder);
        }
        services.TryAddScoped<IAggregateRepository>(provider => new AggregateRepository(provider.GetRequiredService<IEventStore>()));
        services.TryAddScoped<WorkloadContext>();
        services.TryAddScoped<IRequestBus, RequestBus>();
        services.TryAddSingleton<RequestRegistry>();
        services.TryAddSingleton(PortiaEventServiceCollectionExtensions.BuildCatalog);
        services.TryAddSingleton<IDomainEventSerializer, JsonDomainEventSerializer>();
        services.TryAddSingleton<IReactorPrincipalProvider, SystemReactorPrincipalProvider>();
        configure(builder);
        return builder;
    }
}
