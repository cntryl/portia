namespace Cntryl.Portia;

/// <summary>
/// Runs datastore-agnostic projectors in bounded, checkpointed batches.
/// </summary>
/// <param name="reader">The domain-event reader.</param>
public sealed class ProjectorRunner(IDomainEventReader reader)
{
    readonly IDomainEventReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>
    /// Projects all currently readable events beginning at a checkpoint.
    /// </summary>
    /// <typeparam name="TProjection">The projection-specific application port.</typeparam>
    /// <param name="projector">The projector to run.</param>
    /// <param name="checkpoint">The first scope offset to read.</param>
    /// <param name="options">The batching and rebuild options.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The next checkpoint after every committed batch.</returns>
    public async ValueTask<ProjectionCheckpoint> RunAsync<TProjection>(
        Projector<TProjection> projector,
        ProjectionCheckpoint checkpoint,
        ProjectionRunOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projector);
        options ??= ProjectionRunOptions.Default;
        options.Validate();

        var records = new List<DomainEventRecord>(options.MaxBatchSize);
        await foreach (var record in _reader
            .ReadAsync(projector.Pattern, checkpoint.NextOffset, ct)
            .WithCancellation(ct)
            .ConfigureAwait(false))
        {
            records.Add(record);

            if (records.Count == options.MaxBatchSize)
            {
                checkpoint = await CommitAsync(projector, records, checkpoint, options.IsRebuild, ct)
                    .ConfigureAwait(false);
            }
        }

        return records.Count == 0
            ? checkpoint
            : await CommitAsync(projector, records, checkpoint, options.IsRebuild, ct).ConfigureAwait(false);
    }

    static async ValueTask<ProjectionCheckpoint> CommitAsync<TProjection>(
        Projector<TProjection> projector,
        List<DomainEventRecord> records,
        ProjectionCheckpoint checkpoint,
        bool isRebuild,
        CancellationToken ct)
    {
        var context = new ProjectionBatchContext(projector.Name, projector.Pattern, checkpoint, isRebuild);
        await using var batch = await projector.Target.BeginAsync(context, ct).ConfigureAwait(false);

        foreach (var record in records)
            await projector.ProjectAsync(record, batch.Projection, isRebuild, ct).ConfigureAwait(false);

        var lastRecord = records[^1];
        var nextOffset = EventStreamOffsets.GetNextOffset(projector.Pattern, lastRecord);
        var nextCheckpoint = new ProjectionCheckpoint(nextOffset);
        await batch.CommitAsync(nextCheckpoint, ct).ConfigureAwait(false);
        records.Clear();
        return nextCheckpoint;
    }
}
