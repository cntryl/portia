using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
///     Turns a raw bearer token carried alongside a request back into a <see cref="ClaimsPrincipal" />,
///     re-validating it (signature and expiry included) at the point the request is actually
///     dispatched — not just at the point it was first accepted. A queued or scheduled request that
///     outlives its token's lifetime fails authorization rather than executing on a stale grant.
/// </summary>
public interface IRequestActorValidator
{
    /// <summary>
    ///     Validates a raw bearer token and produces the actor it represents.
    /// </summary>
    /// <param name="token">The raw token, or <see langword="null" /> for an unauthenticated actor.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>
    ///     The validated actor on success, or a failure when the token is missing, malformed,
    ///     expired, or otherwise fails validation.
    /// </returns>
    ValueTask<Result<ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default);
}
