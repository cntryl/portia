namespace Cntryl.Portia;

sealed class AuthorizedActionHandler : IRequestHandler<AuthorizedAction>
{
    public bool WasInvoked { get; private set; }

    public ValueTask<Result> HandleAsync(IRequestContext<AuthorizedAction> context, CancellationToken ct)
    {
        WasInvoked = true;
        return ValueTask.FromResult(Result.Success);
    }
}
