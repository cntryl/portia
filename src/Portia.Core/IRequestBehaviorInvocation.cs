namespace Cntryl.Portia;

interface IRequestBehaviorInvocation
{
    ValueTask<Result> InvokeAsync(IServiceProvider services, IRequest request, RequestDispatchContext context,
        RequestPipelineNext continuation, CancellationToken ct);
}
