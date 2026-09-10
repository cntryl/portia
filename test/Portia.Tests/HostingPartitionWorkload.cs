namespace Cntryl.Portia;

sealed class HostingPartitionWorkload(HostingWorkloadState state) : IPartitionWorkload, IAsyncDisposable
{
    readonly Guid _instanceId = Guid.NewGuid();
    string? _partition;

    public ValueTask DisposeAsync()
    {
        if (_partition is { } partition)
        {
            state.StoppedPartitions.Enqueue(partition);
        }

        return ValueTask.CompletedTask;
    }

    public async Task RunAsync(string partition, CancellationToken ct)
    {
        _partition = partition;
        state.StartedPartitions.Enqueue(partition);
        state.WorkloadInstances.Enqueue(_instanceId);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when the lease is lost or the host shuts down.
        }
    }
}
