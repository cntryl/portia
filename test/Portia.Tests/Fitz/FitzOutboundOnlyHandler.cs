namespace Cntryl.Portia;

sealed class FitzOutboundOnlyHandler : IRequestHandler<FitzOutboundOnlyRequest>
{
    public ValueTask<Result> HandleAsync(IRequestContext<FitzOutboundOnlyRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}
