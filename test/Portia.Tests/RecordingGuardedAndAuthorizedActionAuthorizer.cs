namespace Cntryl.Portia;

sealed class RecordingGuardedAndAuthorizedActionAuthorizer(bool grant = true)
    : IRequestAuthorizer<GuardedAndAuthorizedAction>
{
    public bool WasInvoked { get; private set; }

    public ValueTask<Result> AuthorizeAsync(IRequestContext<GuardedAndAuthorizedAction> context, CancellationToken ct)
    {
        WasInvoked = true;
        return ValueTask.FromResult(grant
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Forbidden, "Denied by authorizer.")));
    }
}
