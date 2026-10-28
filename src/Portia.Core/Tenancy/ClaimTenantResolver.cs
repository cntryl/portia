using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
///     Resolves an actor's tenant from one claim on the principal, the common case when the identity
///     provider issues tenant membership. A principal without the claim, with more than one, or whose claim
///     value is not a valid <see cref="TenantId" /> belongs to no tenant.
/// </summary>
/// <param name="claimType">The claim type that carries the tenant, such as <c>tenant_id</c>.</param>
public sealed class ClaimTenantResolver(string claimType) : IActorTenantResolver
{
    readonly string _claimType = string.IsNullOrWhiteSpace(claimType)
        ? throw new ArgumentException("A claim type is required.", nameof(claimType))
        : claimType;

    /// <inheritdoc />
    public ValueTask<TenantId?> ResolveAsync(ClaimsPrincipal actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        TenantId? tenant = null;
        foreach (var claim in actor.FindAll(_claimType))
        {
            if (tenant is not null || !TenantId.TryParse(claim.Value, null, out var parsed))
                return ValueTask.FromResult<TenantId?>(null);
            tenant = parsed;
        }

        return ValueTask.FromResult(tenant);
    }
}
