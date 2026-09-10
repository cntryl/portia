using System.Security.Claims;

namespace Cntryl.Portia;

sealed class RecordingPermissionEvaluator(List<string> calls) : IPermissionEvaluator
{
    public ValueTask<Result> EvaluateAsync(ClaimsPrincipal actor, string permission, CancellationToken ct = default)
    {
        calls.Add("permission");
        return ValueTask.FromResult(Result.Success);
    }
}
