namespace Cntryl.Portia;

/// <summary>
/// Opens datastore-specific projection batches behind an application port.
/// </summary>
/// <typeparam name="TProjection">The projection-specific application port.</typeparam>
public interface IProjectionTarget<TProjection>
{
    /// <summary>
    /// Loads the checkpoint committed atomically with this target's projection data, or the zero
    /// checkpoint if this projector has not committed a batch yet.
    /// </summary>
    /// <param name="projectorName">The stable projector name.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The target's authoritative committed checkpoint.</returns>
    ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(
        string projectorName,
        CancellationToken ct = default);

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
