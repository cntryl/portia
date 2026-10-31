namespace Cntryl.Portia;

/// <summary>Coordinates query connections with the active projection batch on one repository instance.</summary>
sealed class CassieProjectionActivityGate
{
    readonly object _sync = new();
    bool _batchActive;
    int _activeReads;
    TaskCompletionSource _readsDrained = CompletedSource();

    /// <summary>Registers an open read operation or rejects it while a batch is active.</summary>
    internal ReadLease EnterRead()
    {
        lock (_sync)
        {
            if (_batchActive)
                throw new NotSupportedException("Read through the active batch's commands while a projection batch is open.");
            if (_activeReads++ == 0)
                _readsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new ReadLease(this);
        }
    }

    /// <summary>Prevents new reads and waits for already-open query connections to close.</summary>
    internal async ValueTask EnterBatchAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task waitForReads;
        lock (_sync)
        {
            if (_batchActive)
                throw new NotSupportedException("Open one projection batch at a time on a repository.");
            _batchActive = true;
            waitForReads = _activeReads == 0 ? Task.CompletedTask : _readsDrained.Task;
        }

        try
        {
            await waitForReads.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            ExitBatch();
            throw;
        }
    }

    /// <summary>Allows new reads after a batch has been released.</summary>
    internal void ExitBatch()
    {
        lock (_sync)
            _batchActive = false;
    }

    void ExitRead()
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            if (--_activeReads == 0)
                drained = _readsDrained;
        }
        drained?.TrySetResult();
    }

    static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    internal sealed class ReadLease(CassieProjectionActivityGate owner) : IDisposable
    {
        CassieProjectionActivityGate? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ExitRead();
    }
}
