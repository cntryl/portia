namespace Cntryl.Portia;

interface IStreamRequestBehaviorInvocation<TOut>
{
    IAsyncEnumerable<TOut> Invoke(IServiceProvider services, IStreamRequest<TOut> request,
        RequestDispatchContext context, StreamRequestPipelineNext<TOut> continuation, CancellationToken ct);
}
