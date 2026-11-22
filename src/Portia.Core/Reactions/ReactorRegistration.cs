using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Describes how one registered reactor binds, subscribes, loads progress, and runs a pass.</summary>
public sealed class ReactorRegistration : IWorkloadDescriptor
{
    readonly Func<IServiceProvider, Reactor> _resolve;
    readonly EventStreamPattern? _source;
    readonly Type? _eventType;
    readonly Action<IServiceCollection>? _registerHandler;

    ReactorRegistration(Type reactorType, Func<IServiceProvider, Reactor> resolve,
        EventStreamPattern? source = null, Type? eventType = null, Action<IServiceCollection>? registerHandler = null)
    {
        ReactorType = reactorType;
        _resolve = resolve;
        _source = source;
        _eventType = eventType;
        _registerHandler = registerHandler;
    }

    /// <summary>Gets the concrete reactor type.</summary>
    public Type ReactorType { get; }

    Type IWorkloadDescriptor.ComponentType => ReactorType;

    internal string? TypedSubscription { get; private init; }

    bool IWorkloadDescriptor.Matches(IWorkloadDescriptor other) =>
        other is ReactorRegistration registration && _source == registration._source && _eventType == registration._eventType;

    // Reaction effects are not transactional with progress, so a reactor has no separate
    // generation to build into; a rebuild would re-fire every effect.
    bool IWorkloadDescriptor.SupportsRebuild => false;

    string IWorkloadDescriptor.Kind => ProcessorKinds.Reactor;
    void IWorkloadDescriptor.Register(IServiceCollection services)
    {
        _ = services.AddSingleton(this);
        _registerHandler?.Invoke(services);
    }

    void IWorkloadDescriptor.Bind(IServiceProvider services, WorkloadIdentity identity, string? componentName)
        => _resolve(services).BindWorkload(identity, componentName);

    EventStreamPattern IWorkloadDescriptor.Pattern(IServiceProvider services) => _resolve(services).Pattern;

    // A reactor's checkpoint store serves every reactor, so there is no per-component binding to check.
    void IWorkloadDescriptor.Validate(IServiceProvider services, string componentName)
    {
        var checkpoints = _resolve(services).Checkpoints;
        if (checkpoints is not IConditionalProjectionCheckpointStore
            && services.GetService<IOperationalStore>() is null)
            throw new InvalidOperationException(
                $"Reactor '{componentName}' requires a checkpoint store implementing " +
                $"{nameof(IConditionalProjectionCheckpointStore)} for conditional compare-and-advance saves.");
        if (services.GetService<IOperationalStore>() is not null
            && !ReferenceEquals(checkpoints,
                services.GetService<IOperationalReactionCheckpointStore>()))
            throw new InvalidOperationException(
                "Operational reactors must use the registered operational checkpoint store.");
    }

    ValueTask<ProjectionCheckpoint> IWorkloadDescriptor.LoadCheckpoint(IServiceProvider services,
        ProjectionRunOptions options, CancellationToken ct)
    {
        var component = _resolve(services);
        return ProcessorCheckpoints.LoadAsync(component.Checkpoints,
            new CheckpointIdentity(component.Name, component.Pattern), ct);
    }

    async ValueTask<ProjectionPassResult> IWorkloadDescriptor.RunPass(IServiceProvider services,
        ProjectionRunOptions options, CancellationToken ct)
    {
        options.Validate();
        var reactor = _resolve(services);
        WorkloadBinding.Apply(services, reactor.BindWorkload);
        var checkpoint = await ProcessorCheckpoints.LoadAsync(reactor.Checkpoints,
            new CheckpointIdentity(reactor.Name, reactor.Pattern), ct).ConfigureAwait(false);
        return await services.GetRequiredService<ReactorRunner>().ExecutePassAsync(
                services.GetService<OperationalExecution>()?.Session, reactor, checkpoint, options, ct: ct)
            .ConfigureAwait(false);
    }

    /// <summary>Resolves the reactor in the supplied application scope.</summary>
    /// <param name="services">The scope the reactor is resolved from.</param>
    /// <returns>The reactor instance for that scope.</returns>
    public Reactor Resolve(IServiceProvider services) => _resolve(services);

    /// <summary>
    ///     Creates a typed descriptor. This is a pure factory with no container side effects — to
    ///     register and run a reactor, call <c>PortiaBuilder.AddReactor</c>, which builds this
    ///     descriptor and the workload declaration that drives it. Registering a descriptor by hand
    ///     runs nothing.
    /// </summary>
    /// <typeparam name="TReactor">The concrete component.</typeparam>
    /// <returns>The component descriptor.</returns>
    public static ReactorRegistration Create<TReactor>() where TReactor : Reactor => new(
        typeof(TReactor), static services => services.GetRequiredService<TReactor>());

    internal static ReactorRegistration Create<THandler, TEvent>(EventStreamPattern source)
        where THandler : class, IReactorHandler<TEvent>
        where TEvent : DomainEvent
    {
        return new ReactorRegistration(typeof(THandler),
            static services => services.GetRequiredService<HandlerReactor<THandler, TEvent>>(), source, typeof(TEvent),
            services => services.AddScoped(provider => new HandlerReactor<THandler, TEvent>(
                provider.GetRequiredService<THandler>(), provider.GetRequiredService<IProjectionCheckpointStore>(), source)))
        {
            TypedSubscription = HandlerReactor<THandler, TEvent>.Subscription
        };
    }

    sealed class HandlerReactor<THandler, TEvent>(THandler handler, IProjectionCheckpointStore checkpoints,
        EventStreamPattern source) : Reactor(checkpoints, source)
        where THandler : class, IReactorHandler<TEvent>
        where TEvent : DomainEvent
    {
        internal static string Subscription { get; } = $"Cntryl.Portia.IReactorHandler<{typeof(TEvent).FullName ?? typeof(TEvent).Name}>";

        internal override Type ComponentType => typeof(THandler);

        protected override ValueTask<Result> ReactToEventAsync(DomainEventRecord record, IExecutionContext context,
            CancellationToken ct) => record.Event is TEvent trigger
            ? ProcessingObservation.InvokeAsync(Subscription, handler, new ReactorContext<TEvent>(trigger, record, context), ct)
            : ValueTask.FromResult(Result.Success);
    }
}
