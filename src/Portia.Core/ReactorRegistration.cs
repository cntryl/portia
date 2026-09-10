using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Describes how one registered reactor binds, subscribes, loads progress, and runs a pass.</summary>
public sealed class ReactorRegistration : IWorkloadDescriptor
{
    readonly Func<IServiceProvider, Reactor> _resolve;
    readonly Func<IServiceProvider, ProjectionRunOptions, CancellationToken, ValueTask> _runPass;

    ReactorRegistration(Type reactorType, Func<IServiceProvider, Reactor> resolve,
        Func<IServiceProvider, ProjectionRunOptions, CancellationToken, ValueTask> runPass)
    {
        ReactorType = reactorType;
        _resolve = resolve;
        _runPass = runPass;
    }

    /// <summary>Gets the concrete reactor type.</summary>
    public Type ReactorType { get; }

    Type IWorkloadDescriptor.ComponentType => ReactorType;

    // Reaction effects are not transactional with progress, so a reactor has no separate
    // generation to build into; a rebuild would re-fire every effect.
    bool IWorkloadDescriptor.SupportsRebuild => false;
    void IWorkloadDescriptor.Register(IServiceCollection services) => _ = services.AddSingleton(this);

    void IWorkloadDescriptor.Bind(IServiceProvider services, WorkloadIdentity identity, string? componentName)
        => _resolve(services).BindWorkload(identity, componentName);

    EventStreamPattern IWorkloadDescriptor.Pattern(IServiceProvider services) => _resolve(services).Pattern;

    async ValueTask<ProjectionPassResult> IWorkloadDescriptor.RunPass(IServiceProvider services,
        ProjectionRunOptions options, CancellationToken ct)
    {
        options.Validate();
        var reactor = _resolve(services);
        WorkloadBinding.Apply(services, reactor.BindWorkload);
        var checkpoint = await reactor.Checkpoints.LoadAsync(
            new CheckpointIdentity(reactor.Name, reactor.Pattern), ct).ConfigureAwait(false);
        return await services.GetRequiredService<ReactorRunner>()
            .RunPassAsync(reactor, checkpoint, options, ct).ConfigureAwait(false);
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
        typeof(TReactor), static services => services.GetRequiredService<TReactor>(),
        static async (services, options, ct) =>
        {
            options.Validate();
            var reactor = services.GetRequiredService<TReactor>();
            WorkloadBinding.Apply(services, reactor.BindWorkload);
            var checkpoint = await reactor.Checkpoints.LoadAsync(
                new CheckpointIdentity(reactor.Name, reactor.Pattern), ct).ConfigureAwait(false);
            _ = await services.GetRequiredService<ReactorRunner>().RunAsync(
                reactor, checkpoint, options, ct).ConfigureAwait(false);
        });
}
