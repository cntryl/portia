namespace Cntryl.Portia;

sealed class HttpCreateOrderHandler : IRequestHandler<HttpCreateOrder, Uuid>
{
    public ValueTask<Result<Uuid>> HandleAsync(IRequestContext<HttpCreateOrder> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<Uuid>.Success(Uuid.CreateVersion4()));
}
