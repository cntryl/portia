namespace Cntryl.Portia;

static class ProcessorCheckpoints
{
    public static async ValueTask<ProjectionCheckpoint> LoadAsync(IProjectionStore store,
        CheckpointIdentity identity, CancellationToken ct)
    {
        try
        {
            return await store.LoadCheckpointAsync(identity, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProcessorFailureException(identity, null, ProcessorFailureStage.Loading,
                innerException: ex);
        }
    }

    public static async ValueTask<ProjectionCheckpoint> LoadAsync(IProjectionCheckpointStore store,
        CheckpointIdentity identity, CancellationToken ct)
    {
        try
        {
            return await store.LoadAsync(identity, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProcessorFailureException(identity, null, ProcessorFailureStage.Loading,
                innerException: ex);
        }
    }
}
