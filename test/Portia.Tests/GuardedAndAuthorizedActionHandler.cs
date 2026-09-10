namespace Cntryl.Portia;

sealed class GuardedAndAuthorizedActionHandler : IRequestHandler<GuardedAndAuthorizedAction>
{
    public ValueTask<Result> HandleAsync(IRequestContext<GuardedAndAuthorizedAction> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}
