using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia;

/// <summary>Composes shared application services and explicitly activated worker services.</summary>
public sealed class PortiaBuilder
{
    readonly Dictionary<string, Action<IServiceCollection>> _workers = new(StringComparer.Ordinal);
    bool _worker;

    internal PortiaBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>Gets the application's service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Registers an explicitly selected handler using its generated typed descriptors.</summary>
    public PortiaBuilder AddHandler<THandler>() where THandler : class => AddRequestComponent(typeof(THandler), false);

    /// <summary>Registers an explicitly selected authorizer using its generated typed descriptors.</summary>
    public PortiaBuilder AddAuthorizer<TAuthorizer>() where TAuthorizer : class => AddRequestComponent(typeof(TAuthorizer), true);

    /// <summary>Includes an event type in the application's serializer catalog.</summary>
    public PortiaBuilder AddEvent<TEvent>() where TEvent : DomainEvent
    {
        Services.AddPortiaEvent<TEvent>();
        return this;
    }

    /// <summary>Registers transport metadata for an explicit request contract, without a handler.</summary>
    public PortiaBuilder AddRequest<TRequest>() where TRequest : IRequestBase
    {
        AddRequest(typeof(TRequest));
        return this;
    }

    void AddRequest(Type type)
    {
        if (Services.Select(service => service.ImplementationInstance).OfType<RequestTransportRegistration>().Any(item => item.RequestType == type))
            return;
        var generated = type.Assembly.GetType("Cntryl.Portia.PortiaGeneratedServiceCollectionExtensions")?.GetMethod("RegisterRequest");
        if (generated is null)
        {
            if (type.IsDefined(typeof(RequestRouteAttribute), true))
                throw new InvalidOperationException($"Request '{type}' requires Portia.Generators in its declaring project.");
            return;
        }
        _ = generated.Invoke(null, [Services, type]);
    }

    PortiaBuilder AddRequestComponent(Type type, bool authorizer)
    {
        var handlers = Services.Select(service => service.ImplementationInstance).OfType<RequestHandlerRegistration>().ToArray();
        var authorizers = Services.Select(service => service.ImplementationInstance).OfType<RequestAuthorizerRegistration>().ToArray();
        if (authorizer ? authorizers.Any(item => item.AuthorizerType == type) : handlers.Any(item => item.HandlerType == type))
            return this;
        var generated = type.Assembly.GetType("Cntryl.Portia.PortiaGeneratedRequestRegistrations")?.GetMethod("Register")
            ?? throw new InvalidOperationException($"Component '{type}' requires Portia.Generators in its declaring project.");
        var count = Services.Count;
        try
        {
            _ = generated.Invoke(null, [Services, type, authorizer]);
            var newHandlers = Services.Select(service => service.ImplementationInstance).OfType<RequestHandlerRegistration>().ToArray();
            var newAuthorizers = Services.Select(service => service.ImplementationInstance).OfType<RequestAuthorizerRegistration>().ToArray();
            if (!(authorizer ? newAuthorizers.Any(item => item.AuthorizerType == type) : newHandlers.Any(item => item.HandlerType == type)))
                throw new InvalidOperationException($"'{type}' does not implement the selected request component contract.");
            _ = new RequestRegistry(newHandlers, newAuthorizers);
            Services.TryAddScoped(type);
            foreach (var request in newHandlers.Where(item => item.HandlerType == type).Select(item => item.RequestType))
                AddRequest(request);
            return this;
        }
        catch
        {
            while (Services.Count > count)
                Services.RemoveAt(Services.Count - 1);
            throw;
        }
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
        var existing = Services.Select(service => service.ImplementationInstance).OfType<WorkloadRegistration>()
            .FirstOrDefault(item => item.ComponentType == registration.ComponentType || item.Name == registration.Name);
        if (existing is not null)
            return existing == registration ? this : throw new InvalidOperationException($"Conflicting workload registration '{registration.Name}'.");
        Services.TryAddScoped(registration.ComponentType);
        if (descriptor is not null)
        {
            if (!Services.Select(service => service.ImplementationInstance).OfType<ProjectorRegistration>().Any(item => item.ProjectorType == registration.ComponentType))
                _ = Services.AddSingleton(descriptor);
        }
        else
        {
            if (!Services.Select(service => service.ImplementationInstance).OfType<ReactorRegistration>().Any(item => item.ReactorType == registration.ComponentType))
            {
                _ = Services.AddSingleton(new ReactorRegistration(registration.ComponentType,
                    provider => (BaseReactor)provider.GetRequiredService(registration.ComponentType)));
            }
        }
        _ = Services.AddSingleton(registration);
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
