using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia;

/// <summary>Composes shared application services and explicitly activated worker services.</summary>
public sealed class PortiaBuilder
{
    readonly ApplicationComponentCatalog _catalog = new();
    readonly PortiaJsonComposer _json = new();

    internal PortiaBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>Gets the application's service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Configures Portia-owned JSON options before generated contexts are created.</summary>
    public PortiaBuilder ConfigureJson(Action<JsonSerializerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _json.Configure(configure);
        return this;
    }

    /// <summary>Adds a generated JSON context factory to the application resolver chain.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaBuilder AddGeneratedJsonContext(Func<JsonSerializerOptions, JsonSerializerContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _json.AddContext(factory);
        return this;
    }

    internal JsonSerializerOptions BuildJsonOptions() => _json.Build();

    /// <summary>Registers a request handler through Portia.Generators' compile-time typed descriptor.</summary>
    /// <remarks>The generator is supplied by Portia.DependencyInjection.</remarks>
    public PortiaBuilder AddRequestHandler<THandler>() where THandler : class
    {
        _ = Services;
        throw MissingGeneratedRegistration(typeof(THandler), "request handler");
    }

    /// <summary>Registers a request authorizer through Portia.Generators' compile-time typed descriptor.</summary>
    /// <remarks>The generator is supplied by Portia.DependencyInjection.</remarks>
    public PortiaBuilder AddRequestAuthorizer<TAuthorizer>(AuthorizationStage stage = AuthorizationStage.ResourceAccess)
        where TAuthorizer : class
    {
        _ = Services;
        throw MissingGeneratedRegistration(typeof(TAuthorizer), $"request authorizer at stage '{stage}'");
    }

    /// <summary>Registers a typed request pipeline behavior at the given order.</summary>
    /// <remarks>The generator is supplied by Portia.DependencyInjection. Lower orders execute outermost.</remarks>
    public PortiaBuilder AddRequestPipelineBehavior<TBehavior>(int order = 0) where TBehavior : class
    {
        _ = Services;
        throw MissingGeneratedRegistration(typeof(TBehavior), $"request pipeline behavior at order '{order}'");
    }

    /// <summary>Explicitly registers a request whose concrete type is hidden from compile-time dispatch analysis.</summary>
    /// <remarks>Normal strongly typed dispatch is inferred by Portia.Generators. Use this only at dynamic dispatch boundaries.</remarks>
    public PortiaBuilder RegisterDynamicRequest<TRequest>() where TRequest : IRequestBase
    {
        _ = Services;
        throw MissingGeneratedRegistration(typeof(TRequest), "request");
    }

    static InvalidOperationException MissingGeneratedRegistration(Type type, string role) => new(
        $"Portia.Generators did not intercept registration of {role} '{type}'. Ensure Portia.DependencyInjection's analyzer assets are enabled.");

    /// <summary>Includes an event type in the application's serializer catalog.</summary>
    public PortiaBuilder AddEvent<TEvent>() where TEvent : DomainEvent
    {
        _ = Services;
        throw MissingGeneratedRegistration(typeof(TEvent), "domain event");
    }

    /// <summary>Adds a generated versioned domain-event descriptor.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaBuilder AddGeneratedEvent<TEvent>(int version, string name) where TEvent : DomainEvent
    {
        Services.AddPortiaEvent<TEvent>(version, name);
        _json.AddRoot(typeof(TEvent));
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
        if (_catalog.HandlerRequests.TryGetValue(registration.RequestType, out var owner))
        {
            // One handler can serve several requests, so the same registration arriving twice is
            // an idempotent repeat; a different handler for the same request is a conflict.
            return owner == registration.HandlerType ? this
                : throw new InvalidOperationException($"Request '{registration.RequestType}' has conflicting handlers.");
        }
        _catalog.HandlerRequests[registration.RequestType] = registration.HandlerType;
        _json.AddRoot(registration.RequestType);
        if (registration.ResultType is not null)
            _json.AddRoot(registration.ResultType);
        _ = Services.AddSingleton(registration);
        registration.Register(Services);
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
        if (_catalog.Authorizers.TryGetValue(key, out var stage))
        {
            return stage == registration.Stage ? this
                : throw new InvalidOperationException($"Authorizer '{registration.AuthorizerType}' has conflicting stages.");
        }

        _catalog.Authorizers[key] = registration.Stage;
        _ = Services.AddSingleton(registration);
        registration.Register(Services);
        return this;
    }

    /// <summary>Adds a behavior descriptor built by Portia.Generators at compile time.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaBuilder AddGeneratedBehavior(RequestPipelineBehaviorRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var key = (registration.ScopeType, registration.BehaviorType);
        if (_catalog.Behaviors.TryGetValue(key, out var order))
        {
            return order == registration.Order ? this
                : throw new InvalidOperationException($"Pipeline behavior '{registration.BehaviorType}' has conflicting orders.");
        }
        _catalog.Behaviors.Add(key, registration.Order);
        _ = Services.AddSingleton(registration);
        registration.Register(Services);
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
        if (_catalog.Requests.TryAdd(registration.RequestType, registration))
            _ = Services.AddSingleton(registration);
        _json.AddRoot(registration.RequestType);
        if (registration.ResultType is not null)
            _json.AddRoot(registration.ResultType);
        return this;
    }

    internal IEnumerable<RequestTransportRegistration> SelectedRequests(RequestTransports transport) =>
        _catalog.Requests.Values.Where(registration =>
            _catalog.HandlerRequests.ContainsKey(registration.RequestType) && registration.Transports.HasFlag(transport));

    /// <summary>Registers one reactor with an explicitly selected execution scope.</summary>
    public PortiaBuilder AddReactor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TReactor>(WorkloadScope scope, Action<WorkloadOptions>? configure = null)
        where TReactor : BaseReactor
    {
        var registration = new WorkloadRegistration(ReactorRegistration.Create<TReactor>(), scope, configure);
        Services.TryAddScoped<TReactor>();
        return AddWorkload(registration);
    }

    /// <summary>Registers one projector with an explicitly selected execution scope.</summary>
    public PortiaBuilder AddProjector<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProjector>(WorkloadScope scope, Action<WorkloadOptions>? configure = null)
        where TProjector : BaseProjector
    {
        var registration = new WorkloadRegistration(ProjectorRegistration.Create<TProjector>(), scope, configure);
        Services.TryAddScoped<TProjector>();
        return AddWorkload(registration);
    }

    PortiaBuilder AddWorkload(WorkloadRegistration registration)
    {
        if (registration.ComponentType.IsAbstract || registration.ComponentType.ContainsGenericParameters)
            throw new ArgumentException("Register a concrete, closed component type.", nameof(registration));
        if (_catalog.Workloads.TryGetValue(registration.ComponentType, out var existing))
        {
            return Equivalent(existing, registration) ? this
                : throw new InvalidOperationException($"Conflicting workload registration '{registration.Name}'.");
        }
        if (_catalog.WorkloadNames.TryGetValue(registration.Name, out var owner) && owner != registration.ComponentType)
            throw new InvalidOperationException($"Conflicting workload registration '{registration.Name}'.");
        // The descriptor registers itself under its own concrete type, so the builder never has
        // to know which kinds of component exist.
        if (_catalog.ComponentDescriptors.Add(registration.ComponentType))
            registration.Descriptor.Register(Services);
        var workloadDescriptor = ServiceDescriptor.Singleton(registration);
        Services.Add(workloadDescriptor);
        _catalog.Workloads[registration.ComponentType] = registration;
        _catalog.WorkloadNames[registration.Name] = registration.ComponentType;
        Services.TryAddScoped<ProjectorRunner>();
        Services.TryAddScoped<ReactorRunner>();
        return this;

        static bool Equivalent(WorkloadRegistration left, WorkloadRegistration right)
        {
            return left.ComponentType == right.ComponentType && left.Scope == right.Scope && left.Name == right.Name
                && left.ExplicitName == right.ExplicitName && left.PollInterval == right.PollInterval
                && left.Processing == right.Processing;
        }
    }

    /// <summary>Declares named worker-only registrations in shared application setup.</summary>
    public PortiaBuilder ConfigureWorker(string name, Action<IServiceCollection> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        if (_catalog.Workers.TryGetValue(name, out var existing))
        {
            return existing != configure ? throw new InvalidOperationException($"Worker '{name}' has conflicting registrations.") : this;
        }
        if (_catalog.WorkersActivated)
            configure(Services);
        _catalog.Workers.Add(name, configure);
        return this;
    }

    /// <summary>Activates the shared application's worker registrations once in this host.</summary>
    public PortiaBuilder AddWorkers()
    {
        if (_catalog.WorkersActivated)
            return this;
        var staged = new ServiceCollection();
        staged.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, PortiaStartupValidator>());
        foreach (var configure in _catalog.Workers.Values)
            configure(staged);
        _ = staged.AddSingleton<Microsoft.Extensions.Hosting.IHostedService, PortiaWorkloadService>();
        foreach (var descriptor in staged)
            Services.Add(descriptor);
        _catalog.WorkersActivated = true;
        return this;
    }
}

/// <summary>Registers shared Portia application setup in the standard DI container.</summary>
public static class PortiaApplicationServiceCollectionExtensions
{
    static readonly ConditionalWeakTable<IServiceCollection, PortiaBuilder> Builders = [];
    // A private gate, not the caller's IServiceCollection: locking a public object another
    // library may also lock is how unrelated registration code ends up contending.
    static readonly Lock Gate = new();

    /// <summary>Creates or resumes the application's fluent Portia composition root.</summary>
    public static PortiaBuilder AddPortia(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PortiaBuilder builder;
        lock (Gate)
        {
            if (!Builders.TryGetValue(services, out builder!))
            {
                builder = new PortiaBuilder(services);
                Builders.Add(services, builder);
                _ = services.AddSingleton(builder);
            }
        }
        services.TryAddScoped<IAggregateRepository>(provider => new AggregateRepository(provider.GetRequiredService<IEventStore>()));
        services.TryAddScoped<WorkloadContext>();
        services.TryAddScoped<IRequestBus, RequestBus>();
        services.TryAddSingleton<RequestRegistry>();
        services.TryAddSingleton(provider => provider.GetRequiredService<PortiaBuilder>().BuildJsonOptions());
        services.TryAddSingleton(PortiaEventServiceCollectionExtensions.BuildCatalog);
        services.TryAddSingleton<IDomainEventSerializer>(provider => new JsonDomainEventSerializer(
            provider.GetRequiredService<DomainEventTypeCatalog>(), provider.GetServices<IJsonDomainEventUpcaster>(),
            provider.GetRequiredService<JsonSerializerOptions>()));
        services.TryAddSingleton<IReactorPrincipalProvider, SystemReactorPrincipalProvider>();
        return builder;
    }
}
