using System.Runtime.ExceptionServices;

namespace Cntryl.Portia;

/// <summary>
///     Runs datastore-agnostic projectors in bounded, checkpointed batches.
/// </summary>
/// <param name="reader">The domain-event reader.</param>
public sealed class ProjectorRunner(IDomainEventReader reader)
{
    readonly IDomainEventReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>
    ///     Projects one bounded pass beginning at a checkpoint. Resume from the returned checkpoint until a pass makes no progress.
    /// </summary>
    /// <param name="projector">The projector to run.</param>
    /// <param name="checkpoint">The first scope offset to read.</param>
    /// <param name="options">The transactional batch size, source-record pass budget, and rebuild options.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The checkpoint after the last committed batch in this pass; unchanged when no progress was made.</returns>
    public async ValueTask<ProjectionCheckpoint> RunAsync(
        Projector projector,
        ProjectionCheckpoint checkpoint,
        ProjectionRunOptions? options = null,
        CancellationToken ct = default)
        => (await RunPassAsync(null, projector, checkpoint, options, ct).ConfigureAwait(false)).Checkpoint;

    // The journaled form of a pass. Hosting supplies the workload's operational session here, so whether
    // a pass is journaled is decided by its caller's arguments and never by state set on the runner.
    internal async ValueTask<ProjectionCheckpoint> RunAsync(
        OperationalSession session,
        Projector projector,
        ProjectionCheckpoint checkpoint,
        ProjectionRunOptions? options = null,
        CancellationToken ct = default)
        => (await RunPassAsync(session, projector, checkpoint, options, ct).ConfigureAwait(false)).Checkpoint;

    internal async ValueTask<ProjectionPassResult> RunPassAsync(
        OperationalSession? session,
        Projector projector,
        ProjectionCheckpoint checkpoint,
        ProjectionRunOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projector);
        if (projector.Pattern.IsTenantTemplate)
        {
            throw new InvalidOperationException(
                "An unbound tenant stream template cannot be run manually. Register it as a PerTenant workload.");
        }

        options ??= ProjectionRunOptions.Default;
        options.Validate();

        var startingCheckpoint = checkpoint;
        var batchSize = projector.IsBatch ? options.MaxBatchSize : 1;
        var identity = new CheckpointIdentity(projector.Name, projector.Pattern, options.RebuildId);
        var records = new List<DomainEventRecord>(Math.Min(batchSize, options.MaxEventsPerPass));
        var processed = 0;
        var budgetExhausted = false;
        await using var source = new ProcessorEventStream(_reader, identity, checkpoint, ct);
        try
        {
            while (await source.MoveNextAsync().ConfigureAwait(false))
            {
                var record = source.Current;
                records.Add(record);
                processed++;

                if (records.Count == batchSize)
                {
                    checkpoint = await CommitAsync(session, projector, records, checkpoint, identity, ct)
                        .ConfigureAwait(false);
                    source.Checkpoint = checkpoint;
                }

                if (processed >= options.MaxEventsPerPass)
                {
                    budgetExhausted = true;
                    break;
                }
            }

            if (records.Count != 0)
            {
                checkpoint = await CommitAsync(session, projector, records, checkpoint, identity, ct)
                    .ConfigureAwait(false);
            }

            source.Checkpoint = checkpoint;
            session?.EnsureRecoveryCompleted(checkpoint);
            return new ProjectionPassResult(checkpoint, budgetExhausted && checkpoint != startingCheckpoint);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            source.Failure = ex;
            throw;
        }
        catch (ProcessorFailureException ex)
        {
            source.Failure = ex;
            throw;
        }
        catch (Exception ex)
        {
            var failure = new ProcessorFailureException(identity, checkpoint, ProcessorFailureStage.Reading,
                innerException: ex);
            source.Failure = failure;
            throw failure;
        }
    }

    static async ValueTask<ProjectionCheckpoint> CommitAsync(
        OperationalSession? session,
        Projector projector,
        List<DomainEventRecord> records,
        ProjectionCheckpoint checkpoint,
        CheckpointIdentity identity,
        CancellationToken ct)
    {
        var started = PortiaTelemetry.StartTimestamp();
        var count = records.Count;
        var first = records[0];
        var last = records[^1];
        var outcome = "success";
        var stage = ProcessorFailureStage.Beginning;
        var confirmed = checkpoint;
        var committed = false;
        IProjectionBatch? batch = null;
        Exception? failure = null;
        try
        {
            if (session is { } operations)
                _ = await operations.BeginAsync(records, checkpoint, ct).ConfigureAwait(false);
            var context = new ProjectionBatchContext(identity, checkpoint);
            batch = await projector.Store.BeginAsync(context, ct).ConfigureAwait(false);
            stage = ProcessorFailureStage.Handling;
            ct.ThrowIfCancellationRequested();
            using var observation = session?.Observe();
            var result = await projector.ProjectAsync(records, new ProjectorContext(identity), ct)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!result.IsSuccess)
                throw new ProcessorFailureException(identity, checkpoint, stage, first, last, error: result.Error);

            stage = ProcessorFailureStage.Committing;
            var nextCheckpoint = new ProjectionCheckpoint(last.NextCursor);
            await batch.CommitAsync(nextCheckpoint, ct).ConfigureAwait(false);
            committed = true;
            confirmed = nextCheckpoint;
            records.Clear();
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            outcome = "canceled";
            failure = ex;
        }
        catch (Exception ex)
        {
            outcome = "fault";
            var diagnostic = ex as ProcessorFailureException
                             ?? new ProcessorFailureException(identity, checkpoint, stage, first, last,
                                 UninitializedResult.Is(ex)
                                     ? UninitializedResult.ReturnedBy("projector", projector.GetType(), ex)
                                     : ex);
            failure = diagnostic;
        }

        try
        {
            if (batch is not null)
                await batch.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanup) when (failure is not null)
        {
            ProcessorFailureException.RecordCleanup(failure, cleanup);
        }
        catch (Exception cleanup)
        {
            outcome = "fault";
            failure = new ProcessorFailureException(identity, confirmed, ProcessorFailureStage.Disposing,
                first, last, cleanup);
        }
        finally
        {
            PortiaTelemetry.ProcessorBatchFinished(started, projector.Name, "projector", outcome,
                committed ? count : 0, committed ? last.Event.Metadata.OccurredOn : null);
        }

        if (failure is not null)
        {
            if (session is { } operations && !(failure is OperationCanceledException && ct.IsCancellationRequested))
                await operations.RecordFailureAsync(failure, committed ? confirmed : null, ct).ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        if (session is { } confirmation)
        {
            // The repository has already committed, so a confirmation failure belongs to the commit
            // stage and to the checkpoint the repository now holds.
            try
            {
                await confirmation.ConfirmProjectionAsync(confirmed, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw new ProcessorFailureException(identity, confirmed, ProcessorFailureStage.Committing, first,
                    last, ex);
            }
        }

        return confirmed;
    }
}
