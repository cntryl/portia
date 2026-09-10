namespace Cntryl.Portia;

/// <summary>
///     Handles a request that produces a sequence of results. A request must have exactly one handler.
/// </summary>
/// <typeparam name="TRequest">The concrete request type handled.</typeparam>
/// <typeparam name="TOut">The type of each item produced.</typeparam>
public interface IStreamRequestHandler<TRequest, TOut>
    where TRequest : IStreamRequest<TOut>
{
    /// <summary>
    ///     Handles the request.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The items produced by handling the request, streamed as they become available.</returns>
    IAsyncEnumerable<TOut> HandleAsync(IRequestContext<TRequest> context, CancellationToken ct);
}
