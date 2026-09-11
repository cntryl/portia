namespace Cntryl.Portia;

interface IStreamRequestInvocation<TOut>
{
    IAsyncEnumerable<TOut> Invoke(IServiceProvider services, IStreamRequest<TOut> request,
        IRequestContext context, CancellationToken ct);
}
