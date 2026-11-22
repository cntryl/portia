namespace Cntryl.Portia;

interface IProcessorOwnerProbe
{
    ValueTask<ProcessorOwnerStatus> ObserveAsync(WorkloadIdentity identity, CancellationToken ct);
}
