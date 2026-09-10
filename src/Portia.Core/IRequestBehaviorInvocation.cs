namespace Cntryl.Portia;

interface IRequestBehaviorInvocation
{
    ValueTask<Result> InvokeAsync(IServiceProvider services, IRequest request, IRequestContext context,
        RequestPipelineNext continuation, CancellationToken ct);
}
