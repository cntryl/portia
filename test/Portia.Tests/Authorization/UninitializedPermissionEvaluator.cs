using System.Security.Claims;

namespace Cntryl.Portia;

sealed class UninitializedPermissionEvaluator : IPermissionEvaluator
{
    public ValueTask<Result> EvaluateAsync(ClaimsPrincipal actor, string permission, CancellationToken ct = default) =>
        ValueTask.FromResult(default(Result));
}
