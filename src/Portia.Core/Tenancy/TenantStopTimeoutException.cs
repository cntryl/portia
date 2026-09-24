namespace Cntryl.Portia;

/// <summary>Thrown when normal removal cannot stop one tenant within its configured budget.</summary>
/// <param name="tenantId">The tenant whose cleanup exceeded the budget.</param>
/// <param name="timeout">The configured total cleanup budget.</param>
public sealed class TenantStopTimeoutException(TenantId tenantId, TimeSpan timeout)
    // The tenant stays in the TenantId property; the message reaches logs and traces.
    : TimeoutException($"A tenant did not stop within {timeout}.")
{
    /// <summary>Gets the tenant whose cleanup timed out.</summary>
    public TenantId TenantId { get; } = tenantId;

    /// <summary>Gets the configured total cleanup budget.</summary>
    public TimeSpan Timeout { get; } = timeout;
}
