namespace Cntryl.Portia;

/// <summary>
/// Owns a datastore-specific projection unit of work.
/// </summary>
/// <typeparam name="TProjection">The projection-specific application port.</typeparam>
public interface IProjectionBatch<out TProjection> : IAsyncDisposable
{
    /// <summary>
    /// Gets the batch-scoped projection application port.
    /// </summary>
    TProjection Projection { get; }

    /// <summary>
    /// Atomically commits staged projection changes and the next checkpoint.
    /// </summary>
    /// <param name="checkpoint">The next checkpoint after this batch.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the commit.</returns>
    ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default);
}
