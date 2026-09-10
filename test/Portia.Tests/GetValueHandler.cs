namespace Cntryl.Portia;

sealed class GetValueHandler : IRequestHandler<GetValue, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<GetValue> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<int>.Success(7));
}
