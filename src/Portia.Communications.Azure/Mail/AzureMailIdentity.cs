namespace Cntryl.Portia.Communications.Azure;

/// <summary>A tenant's Azure Communication Services sender identity.</summary>
/// <param name="FromAddress">
///     The sender address the tenant's mail is sent from, on a verified domain linked to the
///     Communication Services resource.
/// </param>
public sealed record AzureMailIdentity(string FromAddress);

/// <summary>Resolves a Portia tenant to its Azure Communication Services sender identity.</summary>
public interface IAzureMailTenantResolver
{
    /// <summary>Resolves the sender identity for a tenant.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="ct">A token that can cancel resolution.</param>
    /// <returns>The identity the tenant's mail is sent with.</returns>
    ValueTask<AzureMailIdentity> ResolveAsync(TenantId tenantId, CancellationToken ct = default);
}
