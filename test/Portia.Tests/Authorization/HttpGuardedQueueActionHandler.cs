namespace Cntryl.Portia;

sealed class HttpGuardedQueueActionHandler : IRequestHandler<HttpGuardedQueueAction>
{
    public ValueTask<Result> HandleAsync(IRequestContext<HttpGuardedQueueAction> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}
