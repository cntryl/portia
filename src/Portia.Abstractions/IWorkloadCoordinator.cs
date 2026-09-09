namespace Cntryl.Portia;

/// <summary>Identifies one independently owned application workload.</summary>
public sealed record WorkloadIdentity
{
    /// <summary>Creates an identity for a global workload or one tenant's workload.</summary>
    public WorkloadIdentity(string name, TenantId? tenant = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (tenant is { } value)
            _ = EventStreamPattern.ForPattern(value.Value);
        Name = name;
        Tenant = tenant;
    }

    /// <summary>Gets the application's stable workload name.</summary>
    public string Name { get; }
    /// <summary>Gets the tenant; null denotes a global workload.</summary>
    public TenantId? Tenant { get; }
}

/// <summary>Coordinates ownership of the current set of logical workloads across workers.</summary>
public interface IWorkloadCoordinator
{
    /// <summary>Reconciles changing workloads and runs callbacks only while ownership is held.</summary>
    /// <param name="workloads">Returns a complete current snapshot, including retained identities.</param>
    /// <param name="run">Runs one identity with an ownership cancellation token.
    /// The provider cancels and awaits revoked callbacks before releasing ownership.</param>
    /// <param name="ct">Cancels coordination and every owned callback.</param>
    /// <returns>The complete lifetime, ending only after cancellation or an unrecoverable failure.</returns>
    Task RunAsync(Func<IReadOnlyCollection<WorkloadIdentity>> workloads,
        Func<WorkloadIdentity, CancellationToken, Task> run, CancellationToken ct = default);
}

/// <summary>Provides the identity of the current workload pass to scoped dependencies.</summary>
public sealed class WorkloadContext
{
    WorkloadIdentity? _identity;
    internal bool IsInitialized => _identity is not null;
    /// <summary>Gets the explicit workload identity; unavailable outside a worker pass.</summary>
    public WorkloadIdentity Identity => _identity ?? throw new InvalidOperationException("No workload is executing in this scope.");
    /// <summary>Gets the workload's tenant, or null for a global workload.</summary>
    public TenantId? Tenant => Identity.Tenant;
    internal string? ComponentName { get; private set; }

    internal void Initialize(WorkloadIdentity identity, string? componentName = null)
    {
        if (_identity is not null)
            throw new InvalidOperationException("A workload scope cannot be rebound.");
        _identity = identity;
        ComponentName = componentName;
    }
}
