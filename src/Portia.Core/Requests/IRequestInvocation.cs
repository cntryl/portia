namespace Cntryl.Portia;

interface IRequestInvocation
{
    ValueTask<Result> InvokeAsync(IServiceProvider services, IRequest request, IRequestContext context,
        CancellationToken ct);

    ValueTask<Result> InvokeUnsharedAsync(IServiceProvider services, IRequest request, RequestDispatchContext context,
        CancellationToken ct);
}
