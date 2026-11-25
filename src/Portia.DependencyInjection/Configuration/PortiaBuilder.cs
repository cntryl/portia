using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
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
    readonly Lock _generatedRegistrationGate = new();
    readonly HashSet<object> _generatedRegistrations = [with(ReferenceEqualityComparer.Instance)];
    readonly List<PortiaContractExample> _examples = [];
    readonly PortiaJsonComposer _json = new();
    readonly List<Func<Type, PortiaContractMetadata?>> _metadataProviders = [];
    PortiaContractMetadataRegistry? _contracts;
    bool _requestActorAuthorization;

    internal PortiaBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>Gets the application's service collection.</summary>
    public IServiceCollection Services { get; }

    internal bool AuthorizationRequired => _catalog.AuthorizationRequired;

    /// <summary>Configures Portia-owned JSON options before generated contexts are created.</summary>
    /// <param name="configure">Applied to the options before any generated context is created.</param>
    /// <returns>This builder, for chaining.</returns>
    public PortiaBuilder ConfigureJson(Action<JsonSerializerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _json.Configure(configure);
        return this;
    }

    /// <summary>Registers a named typed example shared by OpenAPI and MCP contract schemas.</summary>
    /// <typeparam name="T">The exact contract type, registered in the application's JSON context.</typeparam>
    /// <param name="name">A name unique within this contract type and direction.</param>
    /// <param name="value">A concrete value serialized with Portia's configured names and converters.</param>
    /// <param name="summary">An optional human-readable summary.</param>
    /// <param name="use">The contract directions illustrated by the example.</param>
    /// <returns>This builder, for chaining.</returns>
    public PortiaBuilder AddExample<T>(
        string name,
        T value,
        string? summary = null,
        PortiaContractUse use = PortiaContractUse.Both
    )
        where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        if (use is PortiaContractUse.None || (use & ~PortiaContractUse.Both) != 0)
            throw new ArgumentOutOfRangeException(nameof(use));

        if (_contracts is not null)
            throw new InvalidOperationException("Contract metadata is already composed.");

        if (_examples.Any(example => example.Type == typeof(T) && example.Name == name && (example.Use & use) != 0))
            throw new InvalidOperationException(
                $"Contract example '{name}' for '{typeof(T)}' is registered more than once.");

        _examples.Add(new PortiaContractExample(typeof(T), name, value, summary, use));
        _json.AddRoot(typeof(T));
        return this;
    }

    /// <summary>Adds a generated JSON context factory to the application resolver chain.</summary>
    /// <param name="factory">Creates the context from the composed Portia JSON options.</param>
    /// <param name="metadata">Optional compile-time metadata for the context contracts.</param>
    /// <returns>This builder, for chaining.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaBuilder AddGeneratedJsonContext(
        Func<JsonSerializerOptions, JsonSerializerContext> factory,
        Func<Type, PortiaContractMetadata?>? metadata = null
    )
    {
        ArgumentNullException.ThrowIfNull(factory);
        _json.AddContext(factory);

        if (metadata is not null)
            AddGeneratedContractMetadata(metadata);

        return this;
    }

    /// <summary>Applies a generated registration block once to this builder; a failed block may be retried.</summary>
    /// <param name="identity">The generated block's private, stable identity object.</param>
    /// <param name="register">Registers the block's typed descriptors.</param>
    /// <returns>This builder, for chaining.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaBuilder AddGeneratedRegistrations(object identity, Action<PortiaBuilder> register)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(register);
        lock (_generatedRegistrationGate)
        {
            if (!_generatedRegistrations.Add(identity))
                return this;

            try
            {
                register(this);
            }
            catch
            {
                _ = _generatedRegistrations.Remove(identity);
                throw;
            }
        }

        return this;
    }

    /// <summary>Adds transport-neutral metadata emitted by the compiler.</summary>
    /// <param name="provider">Resolves compile-time facts and typed accessors.</param>
    /// <returns>This builder, for chaining.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaBuilder AddGeneratedContractMetadata(Func<Type, PortiaContractMetadata?> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (_contracts is not null)
            throw new InvalidOperationException("Contract metadata cannot change after application composition.");

        _metadataProviders.Add(provider);
        return this;
    }

    internal PortiaContractMetadataRegistry Contracts => _contracts ??= new([.. _metadataProviders], [.. _examples]);

    internal JsonSerializerOptions BuildJsonOptions() => _json.Build(Contracts);

    /// <summary>
    ///     Requires every registered request to be protected by an applicable request authorizer or
    ///     <see cref="RequiresPermissionAttribute" /> unless it is allowed anonymous. Hosted startup lists
    ///     unprotected requests, and dispatch refuses them. Repeated calls add to the same requirement.
    /// </summary>
    /// <param name="configure">Allows requests or request families to dispatch anonymously.</param>
    /// <returns>This builder, for chaining.</returns>
    public PortiaBuilder RequireAuthorization(Action<PortiaAuthorizationOptions>? configure = null)
    {
        _catalog.AuthorizationRequired = true;
        configure?.Invoke(new PortiaAuthorizationOptions(_catalog.AnonymousRequests));
        return this;
    }

    /// <summary>Requires authenticated actor attribution for authenticated and system request families.</summary>
    /// <returns>This builder, for chaining.</returns>
    public PortiaBuilder RequireRequestActors()
    {
        if (!_requestActorAuthorization)
        {
            _requestActorAuthorization = true;
            _ = AddGeneratedAuthorizer(new RequestAuthorizerRegistration<IAuthenticatedRequest,
                AuthenticatedRequestActorAuthorizer>(AuthorizationStage.Principal));
            _ = AddGeneratedAuthorizer(new RequestAuthorizerRegistration<ISystemRequest,
                SystemRequestActorAuthorizer>(AuthorizationStage.Principal));
        }

        _ = RequireAuthorization(options => options.AllowAnonymous<IPublicRequest>());
        return this;
    }

    internal RequestAuthorizationRequirement? AuthorizationRequirement() =>
        _catalog.AuthorizationRequired ? new RequestAuthorizationRequirement(_catalog.AnonymousRequests) : null;

    internal bool RequestActorAuthorizationRequired => _requestActorAuthorization;

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

    /// <summary>
    ///     Adds a guard descriptor built by Portia.Generators at compile time. Application code
    ///     calls <see cref="AddRequestGuard{TGuard}" /> instead of this method.
    /// </summary>
    /// <param name="registration">The generated descriptor.</param>
    /// <returns>This builder, for chaining.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaBuilder AddGeneratedGuard(RequestGuardRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (!_catalog.Guards.Add((registration.ScopeType, registration.GuardType)))
        {
            return this;
        }


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

        if (_catalog.Requests.TryGetValue(registration.RequestType, out var existing))
        {
            if (!existing.HasSameContractAs(registration))
            {
                throw registration.ConflictingContract();
            }

            return this;
        }


        _catalog.Requests.Add(registration.RequestType, registration);
        _ = Services.AddSingleton(registration);

        _json.AddRoot(registration.RequestType);

        if (registration.ResultType is not null)
        {
            _json.AddRoot(registration.ResultType);
        }


        return this;
    }

    internal IEnumerable<RequestTransportRegistration> SelectedRequests(RequestTransportId transport) =>
        _catalog.Requests.Values.Where(registration =>
            _catalog.HandlerRequests.ContainsKey(registration.RequestType) &&
            registration.Transports.Contains(transport));

    /// <summary>Declares a durable scheduled request that worker hosts ensure on every startup.</summary>
    public PortiaBuilder AddRequestSchedule<TRequest>(
        TRequest request,
        RequestScheduleSpec spec,
        RequestRouteValues routeValues,
        ClaimsPrincipal actor
    )
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
    /// <param name="name">The stable hosted ownership, checkpoint, and effect identity.</param>
    /// <param name="scope">Whether the reactor runs once globally or once per active tenant.</param>
    /// <param name="configure">
    ///     Adjusts the workload's polling and failure behavior, or <see langword="null" /> for the
    ///     defaults.
    /// </param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>
    ///     Registering a workload also registers Portia's status query for it: a callable
    ///     <c>portia.processor_status</c> request, its handler, and a principal-stage authorizer that denies
    ///     everyone except the configured system-administrator role. It is served only once a transport
    ///     activates worker endpoints. See "Operator inspection of a projector or reactor" in the projectors and reactors guide.
    /// </remarks>
    [SuppressMessage("ApiDesign", "RS0027", Justification = "The reactor overloads have distinct generic arities.")]
    public PortiaBuilder AddReactor<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    TReactor>(
        string name,
        WorkloadScope scope,
        Action<WorkloadOptions>? configure = null
    )
        where TReactor : Reactor
    {
        var registration = new WorkloadRegistration(ReactorRegistration.Create<TReactor>(), name, scope, configure);
        Services.TryAddScoped<TReactor>();
        return AddWorkload(registration);
    }

    /// <summary>Registers a scoped handler for one event type using the host's reactor checkpoint store.</summary>
    /// <typeparam name="THandler">The application handler for this reaction.</typeparam>
    /// <typeparam name="TEvent">The event type selected for this reaction.</typeparam>
    /// <param name="name">The stable hosted ownership and checkpoint identity.</param>
    /// <param name="source">The watched stream pattern. Tenant templates run once per active tenant.</param>
    /// <returns>This builder, for chaining.</returns>
    public PortiaBuilder AddReactor<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler, TEvent>(
            string name,
            EventStreamPattern source
        )
        where THandler : class, IReactorHandler<TEvent>
        where TEvent : DomainEvent => AddReactor<THandler, TEvent>(name, source, null);

    /// <summary>Registers a scoped handler for one event type and configures its workload.</summary>
    /// <typeparam name="THandler">The application handler for this reaction.</typeparam>
    /// <typeparam name="TEvent">The event type selected for this reaction.</typeparam>
    /// <param name="name">The stable hosted ownership and checkpoint identity.</param>
    /// <param name="source">The watched stream pattern. Tenant templates run once per active tenant.</param>
    /// <param name="configure">Workload polling and failure settings.</param>
    /// <returns>This builder, for chaining.</returns>
    public PortiaBuilder AddReactor<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler, TEvent>(
            string name,
            EventStreamPattern source,
            Action<WorkloadOptions>? configure
        )
        where THandler : class, IReactorHandler<TEvent>
        where TEvent : DomainEvent
    {
        ArgumentNullException.ThrowIfNull(source);
        var scope = source.IsTenantTemplate ? WorkloadScope.PerTenant : WorkloadScope.Global;
        var registration = new WorkloadRegistration(ReactorRegistration.Create<THandler, TEvent>(source), name, scope,
            configure);
        _ = AddWorkload(registration);
        Services.TryAddScoped<THandler>();
        return this;
    }

    /// <summary>Registers one projector with an explicitly selected execution scope.</summary>
    /// <typeparam name="TProjector">The concrete projector type.</typeparam>
    /// <param name="name">The stable hosted ownership and checkpoint identity.</param>
    /// <param name="scope">Whether the projector runs once globally or once per active tenant.</param>
    /// <param name="configure">
    ///     Adjusts the workload's polling and failure behavior, or <see langword="null" /> for the
    ///     defaults.
    /// </param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>
    ///     Registering a workload also registers Portia's status query for it: a callable
    ///     <c>portia.processor_status</c> request, its handler, and a principal-stage authorizer that denies
    ///     everyone except the configured system-administrator role. It is served only once a transport
    ///     activates worker endpoints. See "Operator inspection of a projector or reactor" in the projectors and reactors guide.
    /// </remarks>
    public PortiaBuilder AddProjector<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    TProjector>(
        string name,
        WorkloadScope scope,
        Action<WorkloadOptions>? configure = null
    )
        where TProjector : Projector
    {
        var registration = new WorkloadRegistration(ProjectorRegistration.Create<TProjector>(), name, scope, configure);
        Services.TryAddScoped<TProjector>();
        return AddWorkload(registration);
    }

    /// <summary>Registers a plain typed projector for one ordered source.</summary>
    /// <remarks>Subclasses of <see cref="Projector" /> declare their own source and use the overload accepting <see cref="WorkloadScope" />.</remarks>
    /// <typeparam name="TProjector">The application projector.</typeparam>
    /// <param name="name">The workload and checkpoint name.</param>
    /// <param name="source">The event streams to project.</param>
    /// <param name="configure">Optional workload settings.</param>
    /// <returns>This builder.</returns>
    [SuppressMessage("ApiDesign", "RS0026", Justification = "The source and scope overloads have distinct required argument types.")]
    public PortiaBuilder AddProjector<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    TProjector>(string name, EventStreamPattern source, Action<WorkloadOptions>? configure = null)
        where TProjector : class, IProjector
    {
        ArgumentNullException.ThrowIfNull(source);
        var descriptor = new ProjectorRegistration(typeof(TProjector),
            static services => services.GetRequiredService<ScopedDispatch<TProjector>>().Projector)
        {
            Source = source,
            Subscriptions = ProjectorDispatch<TProjector>.Subscriptions
        };
        _ = AddWorkload(new WorkloadRegistration(descriptor, name,
            source.IsTenantTemplate ? WorkloadScope.PerTenant : WorkloadScope.Global, configure));
        Services.TryAddScoped<TProjector>();
        Services.TryAddScoped<ScopedDispatch<TProjector>>(services => new(ProjectorDispatch<TProjector>.Create(
            services.GetRequiredService<TProjector>(), name, source)));
        return this;
    }

    sealed record ScopedDispatch<T>(Projector Projector);

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
        _ = AddProcessorStatus();
        return this;

        static bool Equivalent(WorkloadRegistration left, WorkloadRegistration right)
        {
            return left.ComponentType == right.ComponentType && left.Scope == right.Scope && left.Name == right.Name
                   && left.Descriptor.Matches(right.Descriptor)
                   && left.PollInterval == right.PollInterval
                   && left.FailureAttemptLimit == right.FailureAttemptLimit
                   && left.MaximumFailureDelay == right.MaximumFailureDelay
                   && left.Processing == right.Processing;
        }
    }

    internal PortiaBuilder AddProcessorStatus()
    {
        ProcessorStatusRegistration.Add(this);
        return this;
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

    /// <summary>
    ///     Opts this host into <see cref="SingleProcessWorkloadCoordinator" />, which owns every
    ///     workload in this process. Use it only when exactly one worker replica runs; a scaled-out
    ///     deployment registers a distributed coordinator such as Fitz's instead.
    /// </summary>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>Repeated calls register the coordinator once.</remarks>
    public PortiaBuilder UseSingleProcessWorkloads()
    {
        if (_catalog.SingleProcessWorkloads)
        {
            return this;
        }

        _ = Services.AddSingleton<IWorkloadCoordinator>(_ => new SingleProcessWorkloadCoordinator());
        _catalog.SingleProcessWorkloads = true;
        return this;
    }

    /// <summary>
    ///     Registers the <see cref="ITenantDirectory" /> per-tenant workloads need, read from the
    ///     application's own tenant lifecycle events: a <typeparamref name="TStartEvent" /> activates the
    ///     tenant <paramref name="getTenantId" /> names and a <typeparamref name="TStopEvent" /> removes it.
    ///     The directory reads through the registered <see cref="IDomainEventReader" />, wakes on the
    ///     registered <see cref="IDomainEventNotifier" /> when there is one, and resumes from a registered
    ///     <see cref="ITenantDirectorySnapshotStore" /> when there is one, such as Fitz's
    ///     <c>UseKvTenantSnapshots</c>.
    /// </summary>
    /// <typeparam name="TStartEvent">The event that activates a tenant.</typeparam>
    /// <typeparam name="TStopEvent">The event that removes a tenant.</typeparam>
    /// <param name="pattern">The exact stream pattern the lifecycle events are read from.</param>
    /// <param name="getTenantId">Names the tenant a lifecycle event is about.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>An <see cref="ITenantDirectory" /> the application registered itself is kept.</remarks>
    public PortiaBuilder AddEventSourcedTenantDirectory<TStartEvent, TStopEvent>(
        EventStreamPattern pattern,
        Func<DomainEvent, TenantId> getTenantId
    )
        where TStartEvent : DomainEvent
        where TStopEvent : DomainEvent
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(getTenantId);
        Services.TryAddSingleton<ITenantDirectory>(provider =>
        {
            var reader = provider.GetRequiredService<IDomainEventReader>();
            var notifier = provider.GetService<IDomainEventNotifier>();
            var time = provider.GetService<TimeProvider>();
            return provider.GetService<ITenantDirectorySnapshotStore>() is { } snapshots
                ? TenantDirectorySnapshots.Create<TStartEvent, TStopEvent>(reader, pattern, getTenantId, snapshots,
                    timeProvider: time, notifier: notifier)
                : new EventSourcedTenantDirectory<TStartEvent, TStopEvent>(reader, pattern, getTenantId,
                    timeProvider: time, notifier: notifier);
        });
        return this;
    }

    /// <summary>Activates the shared application's worker registrations once in this host.</summary>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>
    ///     Worker declarations are applied to a copy of the service collection and the copy then replaces the
    ///     collection's contents, so a declaration that throws leaves the application's registrations as they
    ///     were. Descriptors already registered keep their order; nothing the application registered is dropped.
    /// </remarks>
    public PortiaBuilder AddWorkers()
    {
        if (_catalog.WorkersActivated)
        {
            return this;
        }

        // Declarations run against a copy of the application's registrations, so TryAdd* sees them exactly
        // as it would for a declaration made after activation; the copy replaces the collection only once
        // every declaration has succeeded. A declaration that registers through the application's own
        // collection instead (a captured collection, or this builder) adds to it directly, and those
        // registrations are kept.
        var snapshot = new HashSet<ServiceDescriptor>(Services, ReferenceEqualityComparer.Instance);
        var staged = new ServiceCollection();

        foreach (var descriptor in Services)
            staged.Add(descriptor);


        foreach (var configure in _catalog.Workers.Values)
            configure(staged);

        var direct = Services.Where(descriptor => !snapshot.Contains(descriptor)).ToArray();
        _ = staged.AddSingleton<IHostedService, PortiaWorkloadService>();
        Services.Clear();

        foreach (var descriptor in staged)
            Services.Add(descriptor);


        foreach (var descriptor in direct)
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

    /// <summary>Registers a unary request guard through Portia.Generators' compile-time typed descriptor.</summary>
    /// <typeparam name="TGuard">The concrete guard type, known at this call site.</typeparam>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>The generator is supplied by Portia.DependencyInjection.</remarks>
    public PortiaBuilder AddRequestGuard<TGuard>() where TGuard : class =>
        throw MissingGeneratedRegistration(typeof(TGuard), "request guard");

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
