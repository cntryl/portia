namespace Cntryl.Portia;

sealed class RpcChangeValueHandler : IRequestHandler<RpcChangeValue>
{
    public int? LastValue { get; private set; }

    public ValueTask<Result> HandleAsync(IRequestContext<RpcChangeValue> context, CancellationToken ct)
    {
        LastValue = context.Request.Value;
        return ValueTask.FromResult(Result.Success);
    }
}
