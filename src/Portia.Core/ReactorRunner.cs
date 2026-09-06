namespace Cntryl.Portia;

/// <summary>
/// Runs reactors over currently readable events, advancing a checkpoint as each event is
/// dispatched.
/// </summary>
/// <remarks>
/// A reactor's job is raising commands against other aggregates — not generally safe to redo —
/// so redoing already-handled events on a retry risks duplicate side effects.
/// The checkpointed <c>RunAsync</c> overload's <c>checkpointStore</c> parameter (a real store, or
/// <c>InMemoryProjectionCheckpointStore</c> from <c>Portia.Testing</c> for tests/dev) bounds that
/// risk: the checkpoint is durably saved after every <c>maxBatchSize</c> events, not only once
/// the whole pass finishes, so a failure partway through only loses progress back to the start of
/// the *current* batch — mirroring <see cref="ProjectorRunner" />'s own bounded-batch model. Omit
/// it and a failure anywhere in the pass returns no checkpoint at all, exactly as before this
/// existed — that's a real, deliberate choice this class leaves to the caller, not a default it
/// imposes.
/// </remarks>
/// <param name="reader">The domain-event reader.</param>
public sealed class ReactorRunner(IDomainEventReader reader)
{
    const int DefaultMaxBatchSize = 512;

    readonly IDomainEventReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>
    /// Reacts to all currently readable events beginning at a checkpoint.
    /// </summary>
    /// <param name="reactor">The reactor to run.</param>
    /// <param name="checkpoint">The first scope offset to read.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The checkpoint after the last event dispatched.</returns>
    public ValueTask<ProjectionCheckpoint> RunAsync(
        Reactor reactor,
        ProjectionCheckpoint checkpoint,
        CancellationToken ct = default) =>
        RunCoreAsync(reactor, checkpoint, null, DefaultMaxBatchSize, ct);

    /// <summary>
    /// Reacts to all currently readable events, durably saving progress after each bounded batch.
    /// </summary>
    /// <param name="reactor">The reactor to run.</param>
    /// <param name="checkpoint">The first scope offset to read.</param>
    /// <param name="checkpointStore">Saves progress after every batch.</param>
    /// <param name="maxBatchSize">How many events to process between checkpoint saves.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The checkpoint after the last event dispatched.</returns>
    public ValueTask<ProjectionCheckpoint> RunAsync(
        Reactor reactor,
        ProjectionCheckpoint checkpoint,
        IProjectionCheckpointStore checkpointStore,
        int maxBatchSize = DefaultMaxBatchSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpointStore);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchSize);

        return RunCoreAsync(reactor, checkpoint, checkpointStore, maxBatchSize, ct);
    }

    async ValueTask<ProjectionCheckpoint> RunCoreAsync(
        Reactor reactor,
        ProjectionCheckpoint checkpoint,
        IProjectionCheckpointStore? checkpointStore,
        int maxBatchSize,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reactor);

        DomainEventRecord? lastRecord = null;
        var pendingInBatch = 0;

        await foreach (var record in _reader
            .ReadAsync(reactor.Pattern, checkpoint.NextOffset, ct)
            .WithCancellation(ct)
            .ConfigureAwait(false))
        {
            await reactor.ReactAsync(record, ct).ConfigureAwait(false);
            lastRecord = record;
            pendingInBatch++;

            if (checkpointStore is not null && pendingInBatch == maxBatchSize)
            {
                checkpoint = new ProjectionCheckpoint(EventStreamOffsets.GetNextOffset(reactor.Pattern, record));
                await checkpointStore.SaveAsync(new CheckpointIdentity(reactor.Name, reactor.Pattern), checkpoint, ct).ConfigureAwait(false);
                pendingInBatch = 0;
                lastRecord = null;
            }
        }

        if (lastRecord is { } lastReadRecord)
        {
            checkpoint = new ProjectionCheckpoint(EventStreamOffsets.GetNextOffset(reactor.Pattern, lastReadRecord));

            if (checkpointStore is not null)
                await checkpointStore.SaveAsync(new CheckpointIdentity(reactor.Name, reactor.Pattern), checkpoint, ct).ConfigureAwait(false);
        }

        return checkpoint;
    }
}
