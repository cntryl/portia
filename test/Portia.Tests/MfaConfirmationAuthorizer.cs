namespace Cntryl.Portia;

sealed class MfaConfirmationAuthorizer(List<string> calls) : IRequestAuthorizer<IMfaConfirmedRequest>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<IMfaConfirmedRequest> context, CancellationToken ct)
    {
        calls.Add("mfa");
        return ValueTask.FromResult(Result.Success);
    }
}
