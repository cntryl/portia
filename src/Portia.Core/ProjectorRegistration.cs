using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Describes a projector discovered at compile time.</summary>
public sealed class ProjectorRegistration
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

    /// <summary>Creates a typed descriptor for explicit workload registration.</summary>
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
                    projector.BindWorkload(workload.Identity);
                var checkpoint = await projector.Store.LoadCheckpointAsync(new CheckpointIdentity(projector.Name, projector.Pattern, options?.RebuildId), ct).ConfigureAwait(false);
                _ = await services.GetRequiredService<ProjectorRunner>()
                    .RunAsync(projector, checkpoint, options, ct).ConfigureAwait(false);
            });
}
