using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Describes a projector discovered at compile time.</summary>
public sealed class ProjectorRegistration
{
    ProjectorRegistration(
        Type projectorType,
        Type projectionTargetType,
        Func<ProjectorRunner, IServiceProvider, ProjectionCheckpoint, ProjectionRunOptions?, CancellationToken, ValueTask<ProjectionCheckpoint>> run,
        Func<IServiceProvider, ProjectionRunOptions?, CancellationToken, ValueTask> runPass)
    {
        ProjectorType = projectorType;
        ProjectionTargetType = projectionTargetType;
        Run = run;
        RunPass = runPass;
    }

    /// <summary>Gets the concrete projector type.</summary>
    public Type ProjectorType { get; }

    /// <summary>Gets the required projection target service type.</summary>
    public Type ProjectionTargetType { get; }

    /// <summary>Resolves and runs the projector from an explicit checkpoint.</summary>
    public Func<ProjectorRunner, IServiceProvider, ProjectionCheckpoint, ProjectionRunOptions?, CancellationToken, ValueTask<ProjectionCheckpoint>> Run { get; }

    /// <summary>Loads authoritative progress and runs one pass in the supplied scope.</summary>
    public Func<IServiceProvider, ProjectionRunOptions?, CancellationToken, ValueTask> RunPass { get; }

    /// <summary>Creates a typed descriptor for generated module registration.</summary>
    /// <typeparam name="TProjector">The concrete component.</typeparam>
    /// <typeparam name="TProjection">Its projection port.</typeparam>
    /// <returns>The component descriptor.</returns>
    public static ProjectorRegistration Create<TProjector, TProjection>()
        where TProjector : Projector<TProjection> => new(
            typeof(TProjector),
            typeof(IProjectionTarget<TProjection>),
            static (runner, services, checkpoint, options, ct) =>
                runner.RunAsync(services.GetRequiredService<TProjector>(), checkpoint, options, ct),
            static async (services, options, ct) =>
            {
                (options ?? ProjectionRunOptions.Default).Validate();
                var projector = services.GetRequiredService<TProjector>();
                var checkpoint = await projector.Target.LoadCheckpointAsync(new CheckpointIdentity(projector.Name, projector.Pattern, options?.RebuildId), ct).ConfigureAwait(false);
                _ = await services.GetRequiredService<ProjectorRunner>()
                    .RunAsync(projector, checkpoint, options, ct).ConfigureAwait(false);
            });
}
