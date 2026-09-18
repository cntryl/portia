namespace Cntryl.Portia;

interface IStreamRequestBehaviorInvocation<TOut>
{
    IAsyncEnumerable<TOut> Invoke(IServiceProvider services, IStreamRequest<TOut> request,
        IRequestContext context, StreamRequestPipelineNext<TOut> continuation, CancellationToken ct);
}
