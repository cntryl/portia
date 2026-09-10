namespace Cntryl.Portia;

sealed class HostingTenantWorkload(HostingWorkloadState state) : ITenantWorkload, IAsyncDisposable
{
    readonly Guid _instanceId = Guid.NewGuid();
    TenantId? _tenantId;

    public ValueTask DisposeAsync()
    {
        if (_tenantId is { } tenantId)
        {
            state.StoppedTenants.Enqueue(tenantId);
        }

        return ValueTask.CompletedTask;
    }

    public async Task RunAsync(TenantId tenantId, CancellationToken ct)
    {
        _tenantId = tenantId;
        state.StartedTenants.Enqueue(tenantId);
        state.WorkloadInstances.Enqueue(_instanceId);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when the tenant stops or the host shuts down.
        }
    }
}
