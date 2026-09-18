namespace Cntryl.Portia;

sealed class TieredHandler(List<string> calls) : IRequestHandler<TieredRequest>
{
    public ValueTask<Result> HandleAsync(IRequestContext<TieredRequest> context, CancellationToken ct)
    {
        calls.Add("handler");
        return ValueTask.FromResult(Result.Success);
    }
}
