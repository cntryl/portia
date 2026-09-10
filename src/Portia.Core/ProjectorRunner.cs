namespace Cntryl.Portia;

/// <summary>
///     Runs datastore-agnostic projectors in bounded, checkpointed batches.
/// </summary>
/// <param name="reader">The domain-event reader.</param>
public sealed class ProjectorRunner(IDomainEventReader reader)
{
    readonly IDomainEventReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>
    ///     Projects all currently readable events beginning at a checkpoint.
    /// </summary>
    /// <param name="projector">The projector to run.</param>
    /// <param name="checkpoint">The first scope offset to read.</param>
    /// <param name="options">The batching and rebuild options.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The next checkpoint after every committed batch.</returns>
    public async ValueTask<ProjectionCheckpoint> RunAsync(
        Projector projector,
        ProjectionCheckpoint checkpoint,
        ProjectionRunOptions? options = null,
        CancellationToken ct = default)
        => (await RunPassAsync(projector, checkpoint, options, ct).ConfigureAwait(false)).Checkpoint;

    internal async ValueTask<ProjectionPassResult> RunPassAsync(
        Projector projector,
        ProjectionCheckpoint checkpoint,
        ProjectionRunOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projector);
        options ??= ProjectionRunOptions.Default;
        options.Validate();

        var batchSize = projector.IsBatch ? options.MaxBatchSize : 1;
        var records = new List<DomainEventRecord>(batchSize);
        var processed = 0;
        var budgetExhausted = false;
        await foreach (var record in _reader
                           .ReadAsync(projector.Pattern, checkpoint.NextOffset, ct)
                           .WithCancellation(ct)
                           .ConfigureAwait(false))
        {
            records.Add(record);
            processed++;

            if (records.Count == batchSize)
            {
                checkpoint = await CommitAsync(projector, records, checkpoint, options.RebuildId, ct)
                    .ConfigureAwait(false);
            }

            if (processed == options.MaxEventsPerPass)
            {
                budgetExhausted = true;
                break;
            }
        }

        if (records.Count != 0)
        {
            checkpoint = await CommitAsync(projector, records, checkpoint, options.RebuildId, ct)
                .ConfigureAwait(false);
        }

        return new ProjectionPassResult(checkpoint, budgetExhausted);
    }

    static async ValueTask<ProjectionCheckpoint> CommitAsync(
        Projector projector,
        List<DomainEventRecord> records,
        ProjectionCheckpoint checkpoint,
        string? rebuildId,
        CancellationToken ct)
    {
        var started = PortiaTelemetry.StartTimestamp();
        var count = records.Count;
        var lastOccurrence = records[^1].Event.Metadata.OccurredOn;
        var outcome = "success";
        try
        {
            var context =
                new ProjectionBatchContext(new CheckpointIdentity(projector.Name, projector.Pattern, rebuildId),
                    checkpoint);
            await using var batch = await projector.Store.BeginAsync(context, ct).ConfigureAwait(false);

            await projector.ProjectAsync(records, context.Identity, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var lastRecord = records[^1];
            var nextOffset = EventStreamOffsets.GetNextOffset(projector.Pattern, lastRecord);
            var nextCheckpoint = new ProjectionCheckpoint(nextOffset);
            await batch.CommitAsync(nextCheckpoint, ct).ConfigureAwait(false);
            records.Clear();
            return nextCheckpoint;
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled";
            throw;
        }
        catch
        {
            outcome = "fault";
            throw;
        }
        finally
        {
            PortiaTelemetry.ProcessorBatchFinished(started, projector.Name, "projector", outcome, count,
                lastOccurrence);
        }
    }
}
