using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
/// Authorizes a request instance against an actor — the row-level counterpart to
/// <see cref="RequiresPermissionAttribute" />. Discovered the same way as
/// <see cref="IRequestHandler{TRequest}" /> (interface-driven, not naming convention) and
/// invoked automatically by the generated request bus before the request's handler runs, so it
/// applies uniformly across every transport. <typeparamref name="TRequest" /> may be a concrete
/// request, a request-family interface, or <see cref="IRequestBase" />. Every registered
/// authorizer whose scope is assignable from the concrete request runs before its handler.
/// </summary>
/// <typeparam name="TRequest">The concrete request or request-family interface authorized.</typeparam>
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

/// <summary>Orders independent authorization policies without arbitrary numeric priorities.</summary>
public enum AuthorizationStage
{
    /// <summary>Checks properties of the authenticated principal.</summary>
    Principal = 100,
    /// <summary>Checks access to the request's target resource.</summary>
    ResourceAccess = 200,
    /// <summary>Checks elevated or recently confirmed authentication requirements.</summary>
    StepUp = 300,
}
