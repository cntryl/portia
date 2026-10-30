namespace Cntryl.Portia;

/// <summary>Runs reactions and persists progress through the reactor's constructor dependency.</summary>
/// <param name="reader">Reads the durable events the reactor consumes.</param>
/// <param name="principals">
///     Selects the system principal each reaction runs as, or
///     <see langword="null" /> to use Portia's own system principal.
/// </param>
/// <param name="timeProvider">
///     The clock reaction executions are stamped with, or
///     <see langword="null" /> to use <see cref="TimeProvider.System" />.
/// </param>
public sealed class ReactorRunner(
    IDomainEventReader reader,
    IReactorPrincipalProvider? principals = null,
    TimeProvider? timeProvider = null)
{
    internal OperationalSession? Operations { get; set; }

    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    readonly IReactorPrincipalProvider _principals = principals ?? new SystemReactorPrincipalProvider();
    readonly IDomainEventReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>
    ///     Processes one pass, bounded by the default source-record budget. Single-event reactors checkpoint each event; batch reactors
    ///     checkpoint after the bounded batch succeeds. Failures can replay external effects.
    /// </summary>
    /// <param name="reactor">The reactor to run.</param>
    /// <param name="checkpoint">The authoritative progress the pass starts from.</param>
    /// <param name="maxBatchSize">The upper bound on events per committed batch; ignored by single-event reactors. The pass budget remains independent.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The progress reached by this pass. Resume from it until a pass makes no progress.</returns>
    /// <exception cref="InvalidOperationException">The principal provider returned a non-system principal.</exception>
    public async ValueTask<ProjectionCheckpoint> RunAsync(Reactor reactor, ProjectionCheckpoint checkpoint,
        int maxBatchSize = 512, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchSize);
        return await RunPassAsync(reactor, checkpoint,
            ProjectionRunOptions.Default with { MaxBatchSize = maxBatchSize }, ct).ConfigureAwait(false);
    }

    /// <summary>Processes a bounded pass using the supplied batch and pass limits.</summary>
    public async ValueTask<ProjectionCheckpoint> RunPassAsync(Reactor reactor, ProjectionCheckpoint checkpoint,
        ProjectionRunOptions options, CancellationToken ct = default)
        => (await ExecutePassAsync(reactor, checkpoint, options, ct).ConfigureAwait(false)).Checkpoint;

    internal async ValueTask<ProjectionPassResult> ExecutePassAsync(Reactor reactor, ProjectionCheckpoint checkpoint,
        ProjectionRunOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reactor);
        if (reactor.Pattern.IsTenantTemplate)
        {
            throw new InvalidOperationException(
                "An unbound tenant stream template cannot be run manually. Register it as a PerTenant workload.");
        }

        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var actor = PrincipalSnapshot.Copy(_principals.GetPrincipal(reactor));
        if (!RequestActor.IsSystem(actor))
        {
            throw new InvalidOperationException("A reactor principal provider must return a system principal.");
        }

        var startingCheckpoint = checkpoint;
        var identity = new CheckpointIdentity(reactor.Name, reactor.Pattern);
        var batchSize = reactor.IsBatch ? options.MaxBatchSize : 1;
        var contexts = new List<IReactorContext>(Math.Min(batchSize, options.MaxEventsPerPass));
        var processed = 0;
        var budgetExhausted = false;
        await using var source = new ProcessorEventStream(_reader, identity, checkpoint, ct);
        try
        {
            while (await source.MoveNextAsync().ConfigureAwait(false))
            {
                var record = source.Current;
                contexts.Add(ReactionExecutionContext.FromSystemSnapshot(record, actor, _clock));
                processed++;
                if (contexts.Count == batchSize)
                {
                    checkpoint = await CommitAsync().ConfigureAwait(false);
                    source.Checkpoint = checkpoint;
                }

                if (processed >= options.MaxEventsPerPass)
                {
                    budgetExhausted = true;
                    break;
                }
            }

            if (contexts.Count != 0)
            {
                checkpoint = await CommitAsync().ConfigureAwait(false);
            }

            source.Checkpoint = checkpoint;
            Operations?.EnsureRecoveryCompleted(checkpoint);
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


        async ValueTask<ProjectionCheckpoint> CommitAsync()
        {
            var started = PortiaTelemetry.StartTimestamp();
            var count = contexts.Count;
            var lastOccurrence = contexts[^1].Source.Event.Metadata.OccurredOn;
            var outcome = "success";
            var stage = ProcessorFailureStage.Handling;
            try
            {
                if (Operations is not null && reactor.Checkpoints is not IOperationalReactionCheckpointStore)
                    throw new InvalidOperationException("Operations requires the registered operational reactor checkpoint store.");
                if (Operations is { } operations)
                    _ = await operations.BeginAsync(contexts.Select(c => c.Source).ToArray(), checkpoint, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                using var observation = Operations?.Observe();
                if (Operations is not null)
                    foreach (var context in contexts)
                        ProcessingObservation.Record("execution", reactor.Name, context.Source.Event.Metadata.EventId.ToString(),
                        context.ExecutionId.ToString(), "started");
                var result = await reactor.ReactAsync(contexts, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!result.IsSuccess)
                    throw new ProcessorFailureException(identity, checkpoint, stage,
                        contexts[0].Source, contexts[^1].Source, error: result.Error);
                stage = ProcessorFailureStage.Committing;
                var next = new ProjectionCheckpoint(contexts[^1].Source.NextCursor);
                if (Operations is { } operational)
                {
                    if (reactor.Checkpoints is not IOperationalReactionCheckpointStore journal)
                        throw new InvalidOperationException("Operations requires the registered operational reactor checkpoint store.");
                    await operational.CommitReactionAsync(journal, checkpoint, next, ct).ConfigureAwait(false);
                }
                else if (reactor.Checkpoints is IConditionalProjectionCheckpointStore conditional)
                    await conditional.SaveAsync(identity, checkpoint, next, ct).ConfigureAwait(false);
                else
                    await reactor.Checkpoints.SaveAsync(identity, next, ct).ConfigureAwait(false);
                contexts.Clear();
                return next;
            }
            catch (Exception exception)
            {
                outcome = PortiaTelemetry.ExceptionOutcome(exception, ct);
                if (exception is OperationCanceledException && ct.IsCancellationRequested)
                    throw;
                var failure = exception as ProcessorFailureException
                    ?? new ProcessorFailureException(identity, checkpoint, stage,
                        contexts[0].Source, contexts[^1].Source, exception);
                if (Operations is { } operations)
                    await operations.RecordFailureAsync(failure, null, ct).ConfigureAwait(false);
                throw failure;
            }
            finally
            {
                // Only a committed batch processed its events and moved the checkpoint the lag measures.
                var committed = outcome == "success";
                PortiaTelemetry.ProcessorBatchFinished(started, reactor.Name, "reactor", outcome,
                    committed ? count : 0, committed ? lastOccurrence : null);
            }
        }
    }
}
