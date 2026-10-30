namespace Cntryl.Portia;

// Own the enumerator outside the pass's catch boundary so source cleanup cannot replace a handler failure.
sealed class ProcessorEventStream : IAsyncDisposable
{
    readonly IAsyncEnumerator<DomainEventRecord> _source;
    readonly CheckpointIdentity _identity;

    public ProcessorEventStream(IDomainEventReader reader, CheckpointIdentity identity,
        ProjectionCheckpoint checkpoint, CancellationToken ct)
    {
        _identity = identity;
        Checkpoint = checkpoint;
        try
        {
            _source = reader.ReadAsync(identity.Pattern, checkpoint.Cursor, ct).GetAsyncEnumerator(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProcessorFailureException(identity, checkpoint, ProcessorFailureStage.Reading,
                innerException: ex);
        }
    }

    public DomainEventRecord Current => _source.Current;
    public ProjectionCheckpoint Checkpoint { get; set; }
    public Exception? Failure { get; set; }
    public ValueTask<bool> MoveNextAsync() => _source.MoveNextAsync();

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _source.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanup) when (Failure is not null)
        {
            ProcessorFailureException.RecordCleanup(Failure, cleanup);
        }
        catch (Exception cleanup)
        {
            throw new ProcessorFailureException(_identity, Checkpoint, ProcessorFailureStage.Disposing,
                innerException: cleanup);
        }
    }
}
