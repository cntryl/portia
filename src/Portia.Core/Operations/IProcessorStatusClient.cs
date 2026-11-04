namespace Cntryl.Portia;

/// <summary>Asks the owning worker for one projector or reactor's status through its RPC endpoint.</summary>
public interface IProcessorStatusClient
{
    /// <summary>Reads a single component's committed progress and available failure evidence.</summary>
    /// <param name="processor">The component's stable registered workload name.</param>
    /// <param name="tenant">The exact tenant, or null for a global workload.</param>
    /// <param name="actorToken">The caller's credential, validated by the receiving worker.</param>
    /// <param name="ct">Cancels the RPC call.</param>
    /// <returns>The authorized status or ordinary request failure.</returns>
    ValueTask<Result<ProcessorStatus>> GetAsync(string processor, TenantId? tenant = null,
        string? actorToken = null, CancellationToken ct = default);
}
