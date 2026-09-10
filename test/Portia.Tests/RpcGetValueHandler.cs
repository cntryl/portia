namespace Cntryl.Portia;

sealed class RpcGetValueHandler : IRequestHandler<RpcGetValue, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<RpcGetValue> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<int>.Success(7));
}
