namespace Cntryl.Portia;

sealed class AccountRoleAuthorizer(List<string> calls) : IRequestAuthorizer<IAccountRequest>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<IAccountRequest> context, CancellationToken ct)
    {
        calls.Add("account-role");
        return ValueTask.FromResult(Result.Success);
    }
}
