using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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
    /// <param name="configure">Applied to the options before any generated context is created.</param>
    /// <returns>This builder, for chaining.</returns>
    public PortiaBuilder ConfigureJson(Action<JsonSerializerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _json.Configure(configure);
        return this;
    }

    /// <summary>Adds a generated JSON context factory to the application resolver chain.</summary>
    /// <param name="factory">Creates the context from the composed Portia JSON options.</param>
    /// <returns>This builder, for chaining.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaBuilder AddGeneratedJsonContext(Func<JsonSerializerOptions, JsonSerializerContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _json.AddContext(factory);
        return this;
    }

    internal JsonSerializerOptions BuildJsonOptions() => _json.Build();

    static InvalidOperationException MissingGeneratedRegistration(Type type, string role) => new(
        $"Portia.Generators did not intercept registration of {role} '{type}'. Ensure Portia.DependencyInjection's analyzer assets are enabled. "
        + "If the call is inside a generic method, the generator has no concrete type to emit a descriptor for; register each type at its own call site.");

    /// <summary>Adds a generated versioned domain-event descriptor.</summary>
    /// <typeparam name="TEvent">The event type to register.</typeparam>
    /// <param name="version">The event's schema version.</param>
    /// <param name="name">The event's stable logical name.</param>
    /// <returns>This builder, for chaining.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaBuilder AddGeneratedEvent<TEvent>(int version, string name) where TEvent : DomainEvent
    {
        Services.AddPortiaEvent<TEvent>(version, name);
        _json.AddRoot(typeof(TEvent));
        return this;
    }

    /// <summary>
    ///     Adds a handler descriptor built by Portia.Generators at compile time. Application code
    ///     calls <see cref="AddRequestHandler{THandler}" /> instead of this method.
    /// </summary>
    /// <param name="registration">The generated descriptor.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">Another handler is already registered for the request.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaBuilder AddGeneratedHandler(RequestHandlerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (_catalog.HandlerRequests.TryGetValue(registration.RequestType, out var owner))
        {
            // One handler can serve several requests, so the same registration arriving twice is
            // an idempotent repeat; a different handler for the same request is a conflict.
            return owner == registration.HandlerType
                ? this
                : throw new InvalidOperationException(
                    $"Request '{registration.RequestType}' has conflicting handlers.");
        }

        _catalog.HandlerRequests[registration.RequestType] = registration.HandlerType;
        _json.AddRoot(registration.RequestType);
        if (registration.ResultType is not null)
        {
            _json.AddRoot(registration.ResultType);
        }

        _ = Services.AddSingleton(registration);
        registration.Register(Services);
        return this;
    }

    /// <summary>
    ///     Adds an authorizer descriptor built by Portia.Generators at compile time. Application code
    ///     calls <see cref="AddRequestAuthorizer{TAuthorizer}(AuthorizationStage)" /> instead.
    /// </summary>
    /// <param name="registration">The generated descriptor.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">The same authorizer is already registered at another stage.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaBuilder AddGeneratedAuthorizer(RequestAuthorizerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!Enum.IsDefined(registration.Stage))
        {
            throw new ArgumentOutOfRangeException(nameof(registration), registration.Stage,
                "Choose a defined authorization stage.");
        }

        var key = (registration.ScopeType, registration.AuthorizerType);
        if (_catalog.Authorizers.TryGetValue(key, out var stage))
        {
            return stage == registration.Stage
                ? this
                : throw new InvalidOperationException(
                    $"Authorizer '{registration.AuthorizerType}' has conflicting stages.");
        }

        _catalog.Authorizers[key] = registration.Stage;
        _ = Services.AddSingleton(registration);
        registration.Register(Services);
        return this;
    }

    /// <summary>Adds a behavior descriptor built by Portia.Generators at compile time.</summary>
    /// <param name="registration">The generated descriptor.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">The same behavior is already registered at another order.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaBuilder AddGeneratedBehavior(RequestPipelineBehaviorRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var key = (registration.ScopeType, registration.BehaviorType);
        if (_catalog.Behaviors.TryGetValue(key, out var order))
        {
            return order == registration.Order
                ? this
                : throw new InvalidOperationException(
                    $"Pipeline behavior '{registration.BehaviorType}' has conflicting orders.");
        }

        _catalog.Behaviors.Add(key, registration.Order);
        _ = Services.AddSingleton(registration);
        registration.Register(Services);
        return this;
    }

    /// <summary>
    ///     Adds a request's transport metadata, built by Portia.Generators at compile time.
    ///     Normal application code does not call this method: Portia.Generators emits it for typed
    ///     dispatches, handlers, and <see cref="RegisterDynamicRequest{TRequest}" /> escape hatches.
    /// </summary>
    /// <param name="registration">The generated descriptor.</param>
    /// <returns>This builder, for chaining.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaBuilder AddGeneratedRequest(RequestTransportRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (_catalog.Requests.TryAdd(registration.RequestType, registration))
        {
            _ = Services.AddSingleton(registration);
        }

        _json.AddRoot(registration.RequestType);
        if (registration.ResultType is not null)
        {
            _json.AddRoot(registration.ResultType);
        }

        return this;
    }

    internal IEnumerable<RequestTransportRegistration> SelectedRequests(RequestTransports transport) =>
        _catalog.Requests.Values.Where(registration =>
            _catalog.HandlerRequests.ContainsKey(registration.RequestType) &&
            registration.Transports.HasFlag(transport));

    /// <summary>Declares a durable scheduled request that worker hosts ensure on every startup.</summary>
    public PortiaBuilder AddRequestSchedule<TRequest>(TRequest request, RequestScheduleSpec spec,
        RequestRouteValues routeValues, System.Security.Claims.ClaimsPrincipal actor)
        where TRequest : IRequest, ISchedulable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(routeValues);
        ArgumentNullException.ThrowIfNull(actor);
        _ = Services.AddSingleton<IRequestScheduleDeclaration>(
            new RequestScheduleDeclaration<TRequest>(request, spec, routeValues, actor));
        return ConfigureWorker("Portia.RequestSchedules", static services =>
            services.AddSingleton<IHostedService, RequestScheduleStartupService>());
    }

    /// <summary>Registers one reactor with an explicitly selected execution scope.</summary>
    /// <typeparam name="TReactor">The concrete reactor type.</typeparam>
    /// <param name="scope">Whether the reactor runs once globally or once per active tenant.</param>
    /// <param name="configure">Adjusts the workload's polling and failure behavior, or <see langword="null" /> for the defaults.</param>
    /// <returns>This builder, for chaining.</returns>
    public PortiaBuilder AddReactor<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TReactor>(WorkloadScope scope,
        Action<WorkloadOptions>? configure = null)
        where TReactor : Reactor
    {
        var registration = new WorkloadRegistration(ReactorRegistration.Create<TReactor>(), scope, configure);
        Services.TryAddScoped<TReactor>();
        return AddWorkload(registration);
    }

    /// <summary>Registers one projector with an explicitly selected execution scope.</summary>
    /// <typeparam name="TProjector">The concrete projector type.</typeparam>
    /// <param name="scope">Whether the projector runs once globally or once per active tenant.</param>
    /// <param name="configure">Adjusts the workload's polling and failure behavior, or <see langword="null" /> for the defaults.</param>
    /// <returns>This builder, for chaining.</returns>
    public PortiaBuilder AddProjector<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProjector>(WorkloadScope scope,
        Action<WorkloadOptions>? configure = null)
        where TProjector : Projector
    {
        var registration = new WorkloadRegistration(ProjectorRegistration.Create<TProjector>(), scope, configure);
        Services.TryAddScoped<TProjector>();
        return AddWorkload(registration);
    }

    PortiaBuilder AddWorkload(WorkloadRegistration registration)
    {
        if (registration.ComponentType.IsAbstract || registration.ComponentType.ContainsGenericParameters)
        {
            throw new ArgumentException("Register a concrete, closed component type.", nameof(registration));
        }

        if (_catalog.Workloads.TryGetValue(registration.ComponentType, out var existing))
        {
            return Equivalent(existing, registration)
                ? this
                : throw new InvalidOperationException($"Conflicting workload registration '{registration.Name}'.");
        }

        if (_catalog.WorkloadNames.TryGetValue(registration.Name, out var owner) && owner != registration.ComponentType)
        {
            throw new InvalidOperationException($"Conflicting workload registration '{registration.Name}'.");
        }

        // The descriptor registers itself under its own concrete type, so the builder never has
        // to know which kinds of component exist.
        if (_catalog.ComponentDescriptors.Add(registration.ComponentType))
        {
            registration.Descriptor.Register(Services);
        }

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
                   && left.FailureAttemptLimit == right.FailureAttemptLimit
                   && left.MaximumFailureDelay == right.MaximumFailureDelay
                   && left.Processing == right.Processing;
        }
    }

    /// <summary>
    ///     Declares named worker-only registrations in shared application setup. The name is the
    ///     identity: calling this twice under one name keeps the first declaration and ignores the
    ///     second, so shared setup that runs in both an API host and a worker host — or twice on one
    ///     service collection — composes exactly like every other <see cref="PortiaBuilder" /> method.
    /// </summary>
    /// <param name="name">The declaration's unique name within the application.</param>
    /// <param name="configure">Registers the worker-only services.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>
    ///     An earlier version compared the two callbacks and threw when they differed. That could not
    ///     distinguish a genuinely different declaration from the same one supplied again: a lambda
    ///     that captures anything allocates a fresh delegate per call and never compares equal to its
    ///     predecessor, so re-running shared setup threw on its own registrations. There is no way to
    ///     ask whether two delegates mean the same thing, so the name is the contract instead.
    /// </remarks>
    public PortiaBuilder ConfigureWorker(string name, Action<IServiceCollection> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        if (!_catalog.Workers.TryAdd(name, configure))
        {
            return this;
        }

        if (_catalog.WorkersActivated)
        {
            configure(Services);
        }

        return this;
    }

    /// <summary>Activates the shared application's worker registrations once in this host.</summary>
    /// <returns>This builder, for chaining.</returns>
    public PortiaBuilder AddWorkers()
    {
        if (_catalog.WorkersActivated)
        {
            return this;
        }

        var staged = new ServiceCollection();
        foreach (var configure in _catalog.Workers.Values)
            configure(staged);
        _ = staged.AddSingleton<IHostedService, PortiaWorkloadService>();
        foreach (var descriptor in staged)
            Services.Add(descriptor);
        _catalog.WorkersActivated = true;
        return this;
    }

    // The bodies below never run. Portia.Generators replaces each of these call sites with a
    // typed descriptor, which is what makes naming a type that is not a handler a build error
    // rather than a startup failure. They throw so that a call the generator could not reach
    // fails immediately and says why, instead of silently registering nothing.
    //
    // A call it cannot reach is one where the type argument is not known at the call site — most
    // often a generic helper that forwards, such as
    // `static void Register<T>(PortiaBuilder b) => b.AddRequestHandler<T>();`. There is no
    // concrete T there to build a descriptor from. PORTIA018 reports that at compile time; write
    // the registration out per type instead.
    //
    // CA1822 is suppressed rather than satisfied with a throwaway read of Services: these must be
    // instance methods to chain, and a discard written only to fool an analyzer reads like it has
    // a purpose.
#pragma warning disable CA1822
    /// <summary>Registers a request handler through Portia.Generators' compile-time typed descriptor.</summary>
    /// <typeparam name="THandler">The concrete handler type, known at this call site.</typeparam>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>The generator is supplied by Portia.DependencyInjection.</remarks>
    public PortiaBuilder AddRequestHandler<THandler>() where THandler : class =>
        throw MissingGeneratedRegistration(typeof(THandler), "request handler");

    /// <summary>Registers a request authorizer through Portia.Generators' compile-time typed descriptor.</summary>
    /// <typeparam name="TAuthorizer">The concrete authorizer type, known at this call site.</typeparam>
    /// <param name="stage">The pipeline stage the authorizer runs in.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>The generator is supplied by Portia.DependencyInjection.</remarks>
    public PortiaBuilder AddRequestAuthorizer<TAuthorizer>(AuthorizationStage stage = AuthorizationStage.ResourceAccess)
        where TAuthorizer : class =>
        throw MissingGeneratedRegistration(typeof(TAuthorizer), $"request authorizer at stage '{stage}'");

    /// <summary>Registers a typed request pipeline behavior at the given order.</summary>
    /// <typeparam name="TBehavior">The concrete behavior type, known at this call site.</typeparam>
    /// <param name="order">The behavior's position in the pipeline; lower orders execute outermost.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>The generator is supplied by Portia.DependencyInjection. Lower orders execute outermost.</remarks>
    public PortiaBuilder AddRequestPipelineBehavior<TBehavior>(int order = 0) where TBehavior : class =>
        throw MissingGeneratedRegistration(typeof(TBehavior), $"request pipeline behavior at order '{order}'");

    /// <summary>Explicitly registers a request whose concrete type is hidden from compile-time dispatch analysis.</summary>
    /// <typeparam name="TRequest">The concrete request type, known at this call site.</typeparam>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>Normal strongly typed dispatch is inferred by Portia.Generators. Use this only at dynamic dispatch boundaries.</remarks>
    public PortiaBuilder RegisterDynamicRequest<TRequest>() where TRequest : IRequestBase =>
        throw MissingGeneratedRegistration(typeof(TRequest), "request");

    /// <summary>Includes an event type in the application's serializer catalog.</summary>
    /// <typeparam name="TEvent">The concrete event type, known at this call site.</typeparam>
    /// <returns>This builder, for chaining.</returns>
    public PortiaBuilder AddEvent<TEvent>() where TEvent : DomainEvent =>
        throw MissingGeneratedRegistration(typeof(TEvent), "domain event");
#pragma warning restore CA1822
}
