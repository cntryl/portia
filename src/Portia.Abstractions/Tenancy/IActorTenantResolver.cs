using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
///     Names the tenant an authenticated actor belongs to. The application owns where that fact lives: a
///     claim the identity provider issued, a membership store, or a header-derived identity. Register one
///     implementation; <c>TenantRequestAuthorizer</c> compares its answer with the tenant each
///     <see cref="ITenantRequest" /> names.
/// </summary>
public interface IActorTenantResolver
{
    /// <summary>Resolves the actor's tenant.</summary>
    /// <param name="actor">The authenticated principal.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The actor's tenant, or <see langword="null" /> when the actor belongs to none.</returns>
    ValueTask<TenantId?> ResolveAsync(ClaimsPrincipal actor, CancellationToken ct = default);
}
