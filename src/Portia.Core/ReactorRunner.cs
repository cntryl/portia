namespace Cntryl.Portia;

/// <summary>
/// Runs reactors over currently readable events, advancing a checkpoint as each event is
/// dispatched.
/// </summary>
/// <param name="reader">The domain-event reader.</param>
public sealed class ReactorRunner(IDomainEventReader reader)
{
    readonly IDomainEventReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>
    /// Reacts to all currently readable events beginning at a checkpoint.
    /// </summary>
    /// <param name="reactor">The reactor to run.</param>
    /// <param name="checkpoint">The first scope offset to read.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The checkpoint after the last event dispatched.</returns>
    public async ValueTask<ProjectionCheckpoint> RunAsync(
        Reactor reactor,
        ProjectionCheckpoint checkpoint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reactor);
        DomainEventRecord? lastRecord = null;

        await foreach (var record in _reader
            .ReadAsync(reactor.Pattern, checkpoint.NextOffset, ct)
            .WithCancellation(ct)
            .ConfigureAwait(false))
        {
            await reactor.ReactAsync(record, ct).ConfigureAwait(false);
            lastRecord = record;
        }

        return lastRecord is { } lastReadRecord
            ? new ProjectionCheckpoint(EventStreamOffsets.GetNextOffset(reactor.Pattern, lastReadRecord))
            : checkpoint;
    }
}
