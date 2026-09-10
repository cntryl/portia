namespace Cntryl.Portia;

/// <summary>Provides the identity of the current workload pass to scoped dependencies.</summary>
public sealed class WorkloadContext
{
    WorkloadIdentity? _identity;
    internal bool IsInitialized => _identity is not null;

    /// <summary>Gets the explicit workload identity; unavailable outside a worker pass.</summary>
    public WorkloadIdentity Identity =>
        _identity ?? throw new InvalidOperationException("No workload is executing in this scope.");

    /// <summary>Gets the workload's tenant, or null for a global workload.</summary>
    public TenantId? Tenant => Identity.Tenant;

    internal string? ComponentName { get; private set; }

    internal void Initialize(WorkloadIdentity identity, string? componentName = null)
    {
        if (_identity is not null)
        {
            throw new InvalidOperationException("A workload scope cannot be rebound.");
        }

        _identity = identity;
        ComponentName = componentName;
    }
}
