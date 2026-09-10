namespace Cntryl.Portia;

/// <summary>Identifies one independently owned application workload.</summary>
public sealed record WorkloadIdentity
{
    /// <summary>Creates an identity for a global workload or one tenant's workload.</summary>
    public WorkloadIdentity(string name, TenantId? tenant = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (tenant is { } value)
        {
            _ = EventStreamPattern.ForPattern(value.Value);
        }

        Name = name;
        Tenant = tenant;
    }

    /// <summary>Gets the application's stable workload name.</summary>
    public string Name { get; }

    /// <summary>Gets the tenant; null denotes a global workload.</summary>
    public TenantId? Tenant { get; }
}
