namespace Cntryl.Portia;

/// <summary>
/// Carries the batch-scoped projection application port and batch metadata into a projector's
/// event handler.
/// </summary>
/// <typeparam name="TProjection">The projection-specific application port.</typeparam>
public interface IProjectorContext<out TProjection>
{
    /// <summary>
    /// Gets the batch-scoped projection application port.
    /// </summary>
    TProjection Projection { get; }

    /// <summary>
    /// Gets whether the batch belongs to a rebuild generation.
    /// </summary>
    bool IsRebuild { get; }
}

sealed class ProjectorContext<TProjection>(TProjection projection, bool isRebuild) : IProjectorContext<TProjection>
{
    public TProjection Projection { get; } = projection;

    public bool IsRebuild { get; } = isRebuild;
}
