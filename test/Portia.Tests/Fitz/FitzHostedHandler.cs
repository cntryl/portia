namespace Cntryl.Portia;

sealed class FitzHostedHandler : IRequestHandler<FitzHostedRequest>
{
    public ValueTask<Result> HandleAsync(IRequestContext<FitzHostedRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}
