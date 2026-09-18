namespace Cntryl.Portia;

/// <summary>
///     Authorizes a request instance against an actor — the row-level counterpart to
///     <see cref="RequiresPermissionAttribute" />. Discovered the same way as
///     <see cref="IRequestHandler{TRequest}" /> (interface-driven, not naming convention) and
///     invoked automatically by the generated request bus before the request's handler runs, so it
///     applies uniformly across every transport. <typeparamref name="TRequest" /> may be a concrete
///     request, a request-family interface, or <see cref="IRequestBase" />. Every registered
///     authorizer whose scope is assignable from the concrete request runs before its handler.
/// </summary>
/// <typeparam name="TRequest">The concrete request or request-family interface authorized.</typeparam>
public interface IRequestAuthorizer<TRequest>
    where TRequest : IRequestBase
{
    /// <summary>
    ///     Authorizes the request. The actor is <see cref="IExecutionContext.Actor" /> on
    ///     <paramref name="context" />; it is deliberately not a second parameter, so there is one
    ///     answer to who is acting rather than two that a caller could let disagree.
    /// </summary>
    /// <param name="context">The request context, carrying both the request and its actor.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of the authorization.</returns>
    ValueTask<Result> AuthorizeAsync(IRequestContext<TRequest> context, CancellationToken ct);
}
