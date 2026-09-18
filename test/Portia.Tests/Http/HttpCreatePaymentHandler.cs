namespace Cntryl.Portia;

sealed class HttpCreatePaymentHandler : IRequestHandler<HttpCreatePayment, Uuid>
{
    public ValueTask<Result<Uuid>> HandleAsync(IRequestContext<HttpCreatePayment> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<Uuid>.Success(Uuid.CreateVersion4()));
}
