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
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    readonly IReactorPrincipalProvider _principals = principals ?? new SystemReactorPrincipalProvider();
    readonly IDomainEventReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>
    ///     Processes available events. Single-event reactors checkpoint each event; batch reactors
    ///     checkpoint after the bounded batch succeeds. Failures can replay external effects.
    /// </summary>
    /// <param name="reactor">The reactor to run.</param>
    /// <param name="checkpoint">The authoritative progress the pass starts from.</param>
    /// <param name="maxBatchSize">The upper bound on events read per pass; ignored by single-event reactors.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The progress reached by this pass.</returns>
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
        var contexts = new List<IReactorContext>(batchSize);
        var processed = 0;
        var budgetExhausted = false;
        await foreach (var record in _reader.ReadAsync(reactor.Pattern, checkpoint.NextOffset, ct).WithCancellation(ct)
                           .ConfigureAwait(false))
        {
            contexts.Add(ReactionExecutionContext.FromSystemSnapshot(record, actor, _clock));
            processed++;
            if (contexts.Count == batchSize)
            {
                checkpoint = await CommitAsync().ConfigureAwait(false);
            }

            if (processed == options.MaxEventsPerPass)
            {
                budgetExhausted = true;
                break;
            }
        }

        if (contexts.Count != 0)
        {
            checkpoint = await CommitAsync().ConfigureAwait(false);
        }

        return new ProjectionPassResult(checkpoint, budgetExhausted && checkpoint != startingCheckpoint);

        async ValueTask<ProjectionCheckpoint> CommitAsync()
        {
            var started = PortiaTelemetry.StartTimestamp();
            var count = contexts.Count;
            var lastOccurrence = contexts[^1].Source.Event.Metadata.OccurredOn;
            var outcome = "success";
            try
            {
                await reactor.ReactAsync(contexts, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var next = new ProjectionCheckpoint(
                    EventStreamOffsets.GetNextOffset(reactor.Pattern, contexts[^1].Source));
                await reactor.Checkpoints.SaveAsync(identity, next, ct)
                    .ConfigureAwait(false);
                contexts.Clear();
                return next;
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
                PortiaTelemetry.ProcessorBatchFinished(started, reactor.Name, "reactor", outcome, count,
                    lastOccurrence);
            }
        }
    }
}
