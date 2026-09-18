using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
///     Evaluates a declarative, coarse-grained permission (e.g. <c>"users:create"</c>) against an
///     actor. Backs <see cref="RequiresPermissionAttribute" /> — invoked automatically by the
///     generated request bus before a request's handler runs. For anything that depends on the
///     request's own data (row-level authorization), use <see cref="IRequestAuthorizer{TRequest}" />
///     instead.
/// </summary>
public interface IPermissionEvaluator
{
    /// <summary>
    ///     Evaluates whether the actor holds the given permission.
    /// </summary>
    /// <param name="actor">The actor to evaluate.</param>
    /// <param name="permission">
    ///     The permission required, as declared on
    ///     <see cref="RequiresPermissionAttribute" />.
    /// </param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of the evaluation.</returns>
    ValueTask<Result> EvaluateAsync(ClaimsPrincipal actor, string permission, CancellationToken ct = default);
}
