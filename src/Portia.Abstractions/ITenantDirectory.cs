namespace Cntryl.Portia;

/// <summary>
/// Reports which tenants are active and notifies as tenants are added or removed — the tenant
/// control plane, orthogonal to fleet distribution (which worker a component runs on). Each
/// worker runs the same code and can start or stop a per-tenant component for any tenant; this
/// is what decides which tenants that particular worker is currently doing it for.
/// </summary>
public interface ITenantDirectory
{
    /// <summary>
    /// Gets the tenants active right now, read once at startup — before
    /// <see cref="WatchAsync" /> is ever consulted, so a tenant that existed before the caller
    /// started is not missed.
    /// </summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The currently active tenants.</returns>
    IAsyncEnumerable<TenantId> GetActiveTenantsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets tenant lifecycle changes as they happen, live, after startup. Runs until
    /// cancellation is requested.
    /// </summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>Tenant lifecycle changes in the order they occur.</returns>
    IAsyncEnumerable<TenantLifecycleChange> WatchAsync(CancellationToken ct = default);
}

/// <summary>
/// Whether a tenant became active or stopped being active.
/// </summary>
public enum TenantLifecycleChangeKind
{
    /// <summary>
    /// The tenant became active.
    /// </summary>
    Added,

    /// <summary>
    /// The tenant stopped being active.
    /// </summary>
    Removed,
}

/// <summary>
/// One tenant lifecycle change reported by <see cref="ITenantDirectory.WatchAsync" />.
/// </summary>
/// <param name="Kind">Whether the tenant became active or stopped being active.</param>
/// <param name="TenantId">The tenant the change applies to.</param>
public readonly record struct TenantLifecycleChange(TenantLifecycleChangeKind Kind, TenantId TenantId);
