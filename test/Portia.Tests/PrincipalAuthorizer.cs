namespace Cntryl.Portia;

sealed class PrincipalAuthorizer(List<string> calls) : IRequestAuthorizer<IRequestBase>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<IRequestBase> context, CancellationToken ct)
    {
        calls.Add("principal");
        return ValueTask.FromResult(Result.Success);
    }
}
