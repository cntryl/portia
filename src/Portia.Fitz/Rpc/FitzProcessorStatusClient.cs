namespace Cntryl.Portia;

sealed class FitzProcessorStatusClient(IRemoteRequestSender sender, Func<string> application) : IProcessorStatusClient
{
    public ValueTask<Result<ProcessorStatus>> GetAsync(string processor, TenantId? tenant = null,
        string? actorToken = null, CancellationToken ct = default)
    {
        var workload = new WorkloadIdentity(processor, tenant);
        return sender.SendAsync<GetProcessorStatus, ProcessorStatus>(
            new GetProcessorStatus(processor, tenant?.Value), FitzProcessorStatusRoutes.Values(application(), workload),
            actorToken, ct);
    }
}
