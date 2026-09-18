namespace Cntryl.Portia;

sealed class HttpSendPingHandler : IRequestHandler<HttpSendPing>
{
    public ValueTask<Result> HandleAsync(IRequestContext<HttpSendPing> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}
