namespace Cntryl.Portia;

sealed class AuthorizedActionAuthorizer : IRequestAuthorizer<AuthorizedAction>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<AuthorizedAction> context, CancellationToken ct) =>
        ValueTask.FromResult(context.Request.OwnerId == 7
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Forbidden, "Not the owner.")));
}
