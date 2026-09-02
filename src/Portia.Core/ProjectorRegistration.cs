namespace Cntryl.Portia;

/// <summary>
/// Describes one projector discovered at compile time, so a runner can enumerate every
/// projector without runtime assembly scanning or reflection. The projector's projection type
/// is erased behind <see cref="Run" /> since <see cref="ProjectorRegistration" /> is not itself
/// generic.
/// </summary>
/// <param name="projectorType">The concrete projector type.</param>
/// <param name="run">Resolves and runs the projector against a <see cref="ProjectorRunner" />.</param>
public sealed class ProjectorRegistration(
    Type projectorType,
    Func<ProjectorRunner, IServiceProvider, ProjectionCheckpoint, ProjectionRunOptions?, CancellationToken, ValueTask<ProjectionCheckpoint>> run)
{
    /// <summary>
    /// Gets the concrete projector type.
    /// </summary>
    public Type ProjectorType { get; } = projectorType ?? throw new ArgumentNullException(nameof(projectorType));

    /// <summary>
    /// Gets the function that resolves and runs the projector.
    /// </summary>
    public Func<ProjectorRunner, IServiceProvider, ProjectionCheckpoint, ProjectionRunOptions?, CancellationToken, ValueTask<ProjectionCheckpoint>> Run { get; }
        = run ?? throw new ArgumentNullException(nameof(run));
}
