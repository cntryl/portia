namespace Cntryl.Portia;

/// <summary>
///     Runs one tenant-scoped component for as long as a tenant remains active. The hosting adapter
///     resolves a fresh instance from a fresh dependency-injection scope for each tenant and disposes
///     that scope after the run stops.
/// </summary>
public interface ITenantWorkload
{
    /// <summary>
    ///     Runs the component until <paramref name="ct" /> is cancelled or the component completes.
    /// </summary>
    /// <param name="tenantId">The active tenant.</param>
    /// <param name="ct">Cancelled when the tenant stops or the host shuts down.</param>
    /// <returns>A task representing the tenant-scoped run.</returns>
    Task RunAsync(TenantId tenantId, CancellationToken ct);
}
