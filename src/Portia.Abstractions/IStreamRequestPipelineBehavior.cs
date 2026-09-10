namespace Cntryl.Portia;

/// <summary>Wraps enumeration of a matching streaming request handler.</summary>
/// <typeparam name="TRequest">The concrete request type this behavior wraps.</typeparam>
/// <typeparam name="TOut">The type of the values produced.</typeparam>
public interface IStreamRequestPipelineBehavior<in TRequest, TOut> where TRequest : IStreamRequest<TOut>
{
    /// <summary>Returns a stream that wraps the remainder of the pipeline.</summary>
    /// <param name="context">The request context for this enumeration.</param>
    /// <param name="continuation">The rest of the pipeline; skip calling it to short-circuit.</param>
    /// <param name="ct">A token that can cancel enumeration.</param>
    /// <returns>The values yielded to the caller, whether produced here or by the continuation.</returns>
    IAsyncEnumerable<TOut> HandleAsync(IRequestContext<TRequest> context, StreamRequestPipelineNext<TOut> continuation,
        CancellationToken ct);
}
