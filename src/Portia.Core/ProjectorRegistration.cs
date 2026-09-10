using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Describes how one registered projector resolves and runs a pass. Built by
/// <c>PortiaBuilder.AddProjector</c>; applications do not construct it.</summary>
public sealed class ProjectorRegistration : IWorkloadDescriptor
{
    readonly Func<IServiceProvider, Projector> _resolve;

    ProjectorRegistration(
        Type projectorType,
        Func<IServiceProvider, Projector> resolve,
        Func<ProjectorRunner, IServiceProvider, ProjectionCheckpoint, ProjectionRunOptions?, CancellationToken, ValueTask<ProjectionCheckpoint>> run,
        Func<IServiceProvider, ProjectionRunOptions?, CancellationToken, ValueTask> runPass)
    {
        ProjectorType = projectorType;
        _resolve = resolve;
        Run = run;
        RunPass = runPass;
    }

    /// <summary>Gets the concrete projector type.</summary>
    public Type ProjectorType { get; }

    /// <summary>Resolves the projector in the supplied application scope.</summary>
    public Projector Resolve(IServiceProvider services) => _resolve(services);

    /// <summary>Resolves and runs the projector from an explicit checkpoint.</summary>
    public Func<ProjectorRunner, IServiceProvider, ProjectionCheckpoint, ProjectionRunOptions?, CancellationToken, ValueTask<ProjectionCheckpoint>> Run { get; }

    /// <summary>Loads authoritative progress and runs one pass in the supplied scope.</summary>
    public Func<IServiceProvider, ProjectionRunOptions?, CancellationToken, ValueTask> RunPass { get; }

    /// <summary>
    /// Creates a typed descriptor. This is a pure factory with no container side effects — to
    /// register and run a projector, call <c>PortiaBuilder.AddProjector</c>, which builds this
    /// descriptor and the workload declaration that drives it. Registering a descriptor by hand
    /// runs nothing.
    /// </summary>
    /// <typeparam name="TProjector">The concrete component.</typeparam>
    /// <returns>The component descriptor.</returns>
    public static ProjectorRegistration Create<TProjector>()
        where TProjector : Projector => new(
            typeof(TProjector),
            static services => services.GetRequiredService<TProjector>(),
            static (runner, services, checkpoint, options, ct) =>
                runner.RunAsync(services.GetRequiredService<TProjector>(), checkpoint, options, ct),
            static async (services, options, ct) =>
            {
                (options ?? ProjectionRunOptions.Default).Validate();
                var projector = services.GetRequiredService<TProjector>();
                WorkloadBinding.Apply(services, projector.BindWorkload);
                var checkpoint = await projector.Store.LoadCheckpointAsync(new CheckpointIdentity(projector.Name, projector.Pattern, options?.RebuildId), ct).ConfigureAwait(false);
                _ = await services.GetRequiredService<ProjectorRunner>()
                    .RunAsync(projector, checkpoint, options, ct).ConfigureAwait(false);
            });

    Type IWorkloadDescriptor.ComponentType => ProjectorType;

    bool IWorkloadDescriptor.SupportsRebuild => true;

    void IWorkloadDescriptor.Register(IServiceCollection services) => _ = services.AddSingleton(this);

    void IWorkloadDescriptor.Bind(IServiceProvider services, WorkloadIdentity identity, string? componentName)
        => _resolve(services).BindWorkload(identity, componentName);

    EventStreamPattern IWorkloadDescriptor.Pattern(IServiceProvider services) => _resolve(services).Pattern;

    ValueTask IWorkloadDescriptor.RunPass(IServiceProvider services, ProjectionRunOptions options, CancellationToken ct)
        => RunPass(services, options, ct);
}

/// <summary>
/// Binds a component to the workload its scope belongs to. Hosting binds explicitly through
/// <c>IWorkloadDescriptor.Bind</c> before reading a pattern; this covers the public
/// <c>RunPass</c> entry points an application can drive itself, so both descriptor kinds behave
/// the same way when called directly. Re-binding the same identity is a no-op.
/// </summary>
static class WorkloadBinding
{
    public static void Apply(IServiceProvider services, Action<WorkloadIdentity, string?> bind)
    {
        if (services.GetService<WorkloadContext>() is { IsInitialized: true } workload)
            bind(workload.Identity, workload.ComponentName);
    }
}
