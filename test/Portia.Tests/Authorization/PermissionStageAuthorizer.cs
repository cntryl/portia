namespace Cntryl.Portia;

sealed class PermissionStageAuthorizer(List<string> calls) : IRequestAuthorizer<IAccountRequest>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<IAccountRequest> context, CancellationToken ct)
    {
        calls.Add("permission-stage");
        return ValueTask.FromResult(Result.Success);
    }
}
