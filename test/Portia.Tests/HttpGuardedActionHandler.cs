namespace Cntryl.Portia;

sealed class HttpGuardedActionHandler : IRequestHandler<HttpGuardedAction>
{
    public ValueTask<Result> HandleAsync(IRequestContext<HttpGuardedAction> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}
