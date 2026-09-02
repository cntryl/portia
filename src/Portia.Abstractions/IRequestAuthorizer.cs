using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
/// Authorizes a specific request instance against an actor — the row-level counterpart to
/// <see cref="RequiresPermissionAttribute" />. Discovered the same way as
/// <see cref="IRequestHandler{TRequest}" /> (interface-driven, not naming convention) and
/// invoked automatically by the generated request bus before the request's handler runs, so it
/// applies uniformly across every transport. At most one authorizer may be registered per
/// request type; when both this and <see cref="RequiresPermissionAttribute" /> apply to the same
/// request, the coarse-grained permission is checked first.
/// </summary>
/// <typeparam name="TRequest">The concrete request type authorized.</typeparam>
public interface IRequestAuthorizer<TRequest>
    where TRequest : IRequestBase
{
    /// <summary>
    /// Authorizes the request.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="actor">The actor attempting the request.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of the authorization.</returns>
    ValueTask<Result> AuthorizeAsync(IRequestContext<TRequest> context, ClaimsPrincipal actor, CancellationToken ct = default);
}
