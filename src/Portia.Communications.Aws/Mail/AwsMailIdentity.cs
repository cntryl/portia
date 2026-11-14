namespace Cntryl.Portia.Communications.Aws;

/// <summary>A tenant's SES sender identity.</summary>
/// <param name="FromAddress">The verified address, or <c>Name &lt;address&gt;</c>, the tenant's mail is sent from.</param>
/// <param name="ConfigurationSetName">
///     The SES configuration set whose event destination publishes delivery events, or
///     <see langword="null" /> to send without one. Without it SES reports no delivery events.
/// </param>
public sealed record AwsMailIdentity(string FromAddress, string? ConfigurationSetName = null);

/// <summary>Resolves a Portia tenant to its SES sender identity.</summary>
public interface IAwsMailTenantResolver
{
    /// <summary>Resolves the sender identity for a tenant.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="ct">A token that can cancel resolution.</param>
    /// <returns>The identity the tenant's mail is sent with.</returns>
    ValueTask<AwsMailIdentity> ResolveAsync(TenantId tenantId, CancellationToken ct = default);
}
