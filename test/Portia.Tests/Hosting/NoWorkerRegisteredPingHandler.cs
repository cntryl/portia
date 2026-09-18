namespace Cntryl.Portia;

sealed class NoWorkerRegisteredPingHandler : IRequestHandler<NoWorkerRegisteredPing>
{
    public ValueTask<Result> HandleAsync(IRequestContext<NoWorkerRegisteredPing> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}
