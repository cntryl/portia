namespace Cntryl.Portia;

/// <summary>
///     Handles a request that produces no result. A request must have exactly one handler.
/// </summary>
/// <typeparam name="TRequest">The concrete request type handled.</typeparam>
public interface IRequestHandler<TRequest>
    where TRequest : IRequest
{
    /// <summary>
    ///     Handles the request.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of handling the request.</returns>
    ValueTask<Result> HandleAsync(IRequestContext<TRequest> context, CancellationToken ct);
}

/// <summary>
///     Handles a request that produces a result. A request must have exactly one handler.
/// </summary>
/// <typeparam name="TRequest">The concrete request type handled.</typeparam>
/// <typeparam name="TOut">The type of the value produced on success.</typeparam>
public interface IRequestHandler<TRequest, TOut>
    where TRequest : IRequest<TOut>
{
    /// <summary>
    ///     Handles the request.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of handling the request.</returns>
    ValueTask<Result<TOut>> HandleAsync(IRequestContext<TRequest> context, CancellationToken ct);
}
