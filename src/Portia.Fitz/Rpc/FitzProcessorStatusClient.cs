namespace Cntryl.Portia;

sealed class FitzProcessorStatusClient(IRemoteRequestSender sender, Func<string> application, TimeProvider? clock = null)
    : IProcessorStatusClient
{
    public async ValueTask<Result<ProcessorStatus>> GetAsync(string processor, TenantId? tenant = null,
        string? actorToken = null, CancellationToken ct = default)
    {
        var workload = new WorkloadIdentity(processor, tenant);
        var request = new GetProcessorStatus(processor, tenant?.Value);
        var statusNamespace = application();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2), clock ?? TimeProvider.System);
        using var ownerDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await sender.SendAsync<GetProcessorStatus, ProcessorStatus>(request,
                FitzProcessorStatusRoutes.Values(statusNamespace, workload), actorToken, ownerDeadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
        }
        catch (RpcException exception) when (!ct.IsCancellationRequested && exception.DomainCode is
            FitzErrorCodes.RpcWorkerNotFound or FitzErrorCodes.RpcRouteNotRegistered or
            FitzErrorCodes.RpcBackpressure or FitzErrorCodes.RpcTimeout)
        {
        }

        ct.ThrowIfCancellationRequested();
        return await sender.SendAsync<GetProcessorStatus, ProcessorStatus>(request,
            FitzProcessorStatusRoutes.ReaderValues(statusNamespace), actorToken, ct).ConfigureAwait(false);
    }
}
