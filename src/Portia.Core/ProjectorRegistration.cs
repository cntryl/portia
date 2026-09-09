using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Describes how one registered projector resolves and runs a pass. Built by
/// <c>PortiaBuilder.AddProjector</c>; applications do not construct it.</summary>
public sealed class ProjectorRegistration : IWorkloadDescriptor
{
    ProjectorRegistration(
        Type projectorType,
        Func<ProjectorRunner, IServiceProvider, ProjectionCheckpoint, ProjectionRunOptions?, CancellationToken, ValueTask<ProjectionCheckpoint>> run,
        Func<IServiceProvider, ProjectionRunOptions?, CancellationToken, ValueTask> runPass)
    {
        ProjectorType = projectorType;
        Run = run;
        RunPass = runPass;
    }

    /// <summary>Gets the concrete projector type.</summary>
    public Type ProjectorType { get; }

    /// <summary>Resolves and runs the projector from an explicit checkpoint.</summary>
    public Func<ProjectorRunner, IServiceProvider, ProjectionCheckpoint, ProjectionRunOptions?, CancellationToken, ValueTask<ProjectionCheckpoint>> Run { get; }

    /// <summary>Loads authoritative progress and runs one pass in the supplied scope.</summary>
    public Func<IServiceProvider, ProjectionRunOptions?, CancellationToken, ValueTask> RunPass { get; }

    Type IWorkloadDescriptor.ComponentType => ProjectorType;

    void IWorkloadDescriptor.Bind(IServiceProvider services, WorkloadIdentity identity, string? componentName)
        => services.GetRequiredService(ProjectorType).AsProjector().BindWorkload(identity, componentName);

    EventStreamPattern IWorkloadDescriptor.Pattern(IServiceProvider services)
        => services.GetRequiredService(ProjectorType).AsProjector().Pattern;

    ValueTask IWorkloadDescriptor.RunPass(IServiceProvider services, ProjectionRunOptions options, CancellationToken ct)
        => RunPass(services, options, ct);

    /// <summary>
    /// Creates a typed descriptor. This is a pure factory with no container side effects — to
    /// register and run a projector, call <c>PortiaBuilder.AddProjector</c>, which builds this
    /// descriptor and the workload declaration that drives it. Registering a descriptor by hand
    /// runs nothing.
    /// </summary>
    /// <typeparam name="TProjector">The concrete component.</typeparam>
    /// <returns>The component descriptor.</returns>
    public static ProjectorRegistration Create<TProjector>()
        where TProjector : BaseProjector => new(
            typeof(TProjector),
            static (runner, services, checkpoint, options, ct) =>
                runner.RunAsync(services.GetRequiredService<TProjector>(), checkpoint, options, ct),
            static async (services, options, ct) =>
            {
                (options ?? ProjectionRunOptions.Default).Validate();
                var projector = services.GetRequiredService<TProjector>();
                if (services.GetService<WorkloadContext>() is { IsInitialized: true } workload)
                    projector.BindWorkload(workload.Identity, workload.ComponentName);
                var checkpoint = await projector.Store.LoadCheckpointAsync(new CheckpointIdentity(projector.Name, projector.Pattern, options?.RebuildId), ct).ConfigureAwait(false);
                _ = await services.GetRequiredService<ProjectorRunner>()
                    .RunAsync(projector, checkpoint, options, ct).ConfigureAwait(false);
            });
}

static class ProjectorDescriptorExtensions
{
    internal static BaseProjector AsProjector(this object value) => (BaseProjector)value;
}
