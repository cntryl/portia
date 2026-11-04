namespace Cntryl.Portia;

/// <summary>Hosts one processor status endpoint for the lifetime of its workload ownership.</summary>
public interface IProcessorStatusRpcRegistrar
{
    /// <summary>Registers one exact component and tenant endpoint while the caller owns that workload.</summary>
    /// <param name="workload">The owned component and exact tenant, or a global component.</param>
    /// <param name="ct">Cancels endpoint registration.</param>
    /// <returns>A registration disposed before workload ownership is released.</returns>
    ValueTask<IAsyncDisposable> RegisterAsync(WorkloadIdentity workload, CancellationToken ct = default);
}
