namespace Cntryl.Portia;

/// <summary>Runs reactions and persists progress through the reactor's constructor dependency.</summary>
public sealed class ReactorRunner(IDomainEventReader reader, IReactorPrincipalProvider? principals = null, TimeProvider? timeProvider = null)
{
    readonly IDomainEventReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    readonly IReactorPrincipalProvider _principals = principals ?? new SystemReactorPrincipalProvider();
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>Processes available events. Single-event reactors checkpoint each event; batch reactors
    /// checkpoint after the bounded batch succeeds. Failures can replay external effects.</summary>
    public async ValueTask<ProjectionCheckpoint> RunAsync(Reactor reactor, ProjectionCheckpoint checkpoint,
        int maxBatchSize = 512, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reactor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchSize);
        var actor = _principals.GetPrincipal(reactor);
        if (!RequestActor.IsSystem(actor))
            throw new InvalidOperationException("A reactor principal provider must return a system principal.");
        var batchSize = reactor.IsBatch ? maxBatchSize : 1;
        var contexts = new List<IReactorContext>(batchSize);
        await foreach (var record in _reader.ReadAsync(reactor.Pattern, checkpoint.NextOffset, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            contexts.Add(new ReactionExecutionContext(record, actor, _clock));
            if (contexts.Count == batchSize)
                checkpoint = await CommitAsync().ConfigureAwait(false);
        }
        return contexts.Count == 0 ? checkpoint : await CommitAsync().ConfigureAwait(false);

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
                var next = new ProjectionCheckpoint(EventStreamOffsets.GetNextOffset(reactor.Pattern, contexts[^1].Source));
                await reactor.Checkpoints.SaveAsync(new CheckpointIdentity(reactor.Name, reactor.Pattern), next, ct).ConfigureAwait(false);
                contexts.Clear();
                return next;
            }
            catch (OperationCanceledException) { outcome = "canceled"; throw; }
            catch { outcome = "fault"; throw; }
            finally { PortiaTelemetry.ProcessorBatchFinished(started, reactor.Name, "reactor", outcome, count, lastOccurrence); }
        }
    }
}
