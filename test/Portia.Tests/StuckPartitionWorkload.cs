namespace Cntryl.Portia;

sealed class StuckPartitionWorkload : IPartitionWorkload
{
    readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;

    public Task RunAsync(string partition, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(partition) || !ct.CanBeCanceled)
        {
            throw new InvalidOperationException("The hosted workload did not receive lease-scoped state.");
        }

        _started.SetResult();
        return _release.Task;
    }

    public void Release() => _release.SetResult();
}
