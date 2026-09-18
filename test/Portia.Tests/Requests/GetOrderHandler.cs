namespace Cntryl.Portia;

sealed class GetOrderHandler : IRequestHandler<GetOrder, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<GetOrder> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<int>.Success(context.Request.OrderId));
}
