namespace Cntryl.Portia;

/// <summary>Marks a request that completes with one outcome.</summary>
public interface IUnaryRequest : IRequestBase
{
    internal ValueTask<Result> DispatchWithoutValueAsync(
        IRequestBus bus,
        RequestDispatchContext context,
        CancellationToken ct
    ) => throw new InvalidOperationException("Implement IRequest or IRequest<T> to dispatch a unary request.");
}

/// <summary>
///     Marks a request dispatched through the request bus that produces no result.
/// </summary>
public interface IRequest : IUnaryRequest;

/// <summary>
///     Marks a request dispatched through the request bus that produces a result.
/// </summary>
/// <typeparam name="TOut">The type of the result produced by handling the request.</typeparam>
public interface IRequest<TOut> : IUnaryRequest
{
    async ValueTask<Result> IUnaryRequest.DispatchWithoutValueAsync(
        IRequestBus bus,
        RequestDispatchContext context,
        CancellationToken ct
    )
    {
        var result = await bus.DispatchAsync(this, context, ct).ConfigureAwait(false);
        return result.IsSuccess ? Result.Success : Result.Failure(result.Error);
    }
}
