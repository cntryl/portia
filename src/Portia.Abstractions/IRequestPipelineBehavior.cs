namespace Cntryl.Portia;

// Named for what they are — the rest of the pipeline, waiting to be called — rather than
// RequestHandler/StreamRequestHandler, which read as siblings of IRequestHandler and
// IStreamRequestHandler while being something else entirely.

/// <summary>Continues a result-bearing request pipeline.</summary>
/// <typeparam name="TOut">The type of the value produced on success.</typeparam>
/// <param name="ct">A token that can cancel the remainder of the pipeline.</param>
/// <returns>The outcome produced by the rest of the pipeline.</returns>
public delegate ValueTask<Result<TOut>> RequestPipelineNext<TOut>(CancellationToken ct);

/// <summary>Wraps execution of a matching no-result request handler.</summary>
/// <typeparam name="TRequest">The concrete request type this behavior wraps.</typeparam>
public interface IRequestPipelineBehavior<in TRequest> where TRequest : IRequest
{
    /// <summary>Executes this behavior and, when appropriate, the remainder of the pipeline.</summary>
    /// <param name="context">The request context for this execution.</param>
    /// <param name="continuation">The rest of the pipeline; skip calling it to short-circuit.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of the request, whether produced here or by the continuation.</returns>
    ValueTask<Result> HandleAsync(IRequestContext<TRequest> context, RequestPipelineNext continuation,
        CancellationToken ct);
}

/// <summary>Wraps execution of a matching result-bearing request handler.</summary>
/// <typeparam name="TRequest">The concrete request type this behavior wraps.</typeparam>
/// <typeparam name="TOut">The type of the value produced on success.</typeparam>
public interface IRequestPipelineBehavior<in TRequest, TOut> where TRequest : IRequest<TOut>
{
    /// <summary>Executes this behavior and, when appropriate, the remainder of the pipeline.</summary>
    /// <param name="context">The request context for this execution.</param>
    /// <param name="continuation">The rest of the pipeline; skip calling it to short-circuit.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of the request, whether produced here or by the continuation.</returns>
    ValueTask<Result<TOut>> HandleAsync(IRequestContext<TRequest> context, RequestPipelineNext<TOut> continuation,
        CancellationToken ct);
}
