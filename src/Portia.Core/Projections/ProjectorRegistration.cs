using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Describes how one registered projector resolves and runs a pass. Built by
///     <c>PortiaBuilder.AddProjector</c>; applications do not construct it.
/// </summary>
public sealed class ProjectorRegistration : IWorkloadDescriptor
{
    readonly Func<IServiceProvider, Projector> _resolve;

    internal ProjectorRegistration(Type projectorType, Func<IServiceProvider, Projector> resolve)
    {
        ProjectorType = projectorType;
        _resolve = resolve;
    }

    internal EventStreamPattern? Source { get; init; }
    internal IReadOnlyList<string>? Subscriptions { get; init; }

    bool IWorkloadDescriptor.Matches(IWorkloadDescriptor other) =>
        other is ProjectorRegistration descriptor && Source == descriptor.Source;

    /// <summary>Gets the concrete projector type.</summary>
    public Type ProjectorType { get; }

    Type IWorkloadDescriptor.ComponentType => ProjectorType;

    bool IWorkloadDescriptor.SupportsRebuild => true;

    string IWorkloadDescriptor.Kind => ProcessorKinds.Projector;

    void IWorkloadDescriptor.Register(IServiceCollection services) => _ = services.AddSingleton(this);

    void IWorkloadDescriptor.Bind(IServiceProvider services, WorkloadIdentity identity, string? componentName)
        => _resolve(services).BindWorkload(identity, componentName);

    EventStreamPattern IWorkloadDescriptor.Pattern(IServiceProvider services) => _resolve(services).Pattern;

    void IWorkloadDescriptor.Validate(IServiceProvider services, string componentName)
    {
        if (_resolve(services).Store is IProjectorBoundStore store)
            store.EnsureServes(componentName);
    }

    ValueTask<ProjectionCheckpoint> IWorkloadDescriptor.LoadCheckpoint(
        IServiceProvider services,
        ProjectionRunOptions options,
        CancellationToken ct
    )
    {
        var component = _resolve(services);
        return ProcessorCheckpoints.LoadAsync(component.Store,
            new CheckpointIdentity(component.Name, component.Pattern, options.RebuildId), ct);
    }

    async ValueTask<ProjectionPassResult> IWorkloadDescriptor.RunPass(
        IServiceProvider services,
        ProjectionRunOptions options,
        CancellationToken ct
    )
    {
        options.Validate();
        var projector = _resolve(services);
        WorkloadBinding.Apply(services, projector.BindWorkload);
        var checkpoint = await ProcessorCheckpoints.LoadAsync(projector.Store,
            new CheckpointIdentity(projector.Name, projector.Pattern, options.RebuildId), ct).ConfigureAwait(false);
        return await services.GetRequiredService<ProjectorRunner>().RunPassAsync(
                services.GetService<OperationalExecution>()?.Session, projector, checkpoint, options, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Resolves the projector in the supplied application scope.</summary>
    /// <param name="services">The scope the projector is resolved from.</param>
    /// <returns>The projector instance for that scope.</returns>
    public Projector Resolve(IServiceProvider services) => _resolve(services);

    /// <summary>
    ///     Creates a typed descriptor. This is a pure factory with no container side effects — to
    ///     register and run a projector, call <c>PortiaBuilder.AddProjector</c>, which builds this
    ///     descriptor and the workload declaration that drives it. Registering a descriptor by hand
    ///     runs nothing.
    /// </summary>
    /// <typeparam name="TProjector">The concrete component.</typeparam>
    /// <returns>The component descriptor.</returns>
    public static ProjectorRegistration Create<TProjector>()
        where TProjector : Projector => new(
        typeof(TProjector), static services => services.GetRequiredService<TProjector>());
}
