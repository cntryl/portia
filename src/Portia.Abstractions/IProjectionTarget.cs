namespace Cntryl.Portia;

/// <summary>
/// Opens datastore-specific projection batches behind an application port.
/// </summary>
/// <typeparam name="TProjection">The projection-specific application port.</typeparam>
public interface IProjectionTarget<TProjection>
{
    /// <summary>
    /// Begins a projection batch.
    /// </summary>
    /// <param name="context">The batch context.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The opened projection batch.</returns>
    ValueTask<IProjectionBatch<TProjection>> BeginAsync(
        ProjectionBatchContext context,
        CancellationToken ct = default);
}
