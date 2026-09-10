namespace Cntryl.Portia;

// Named for what they are — the rest of the pipeline, waiting to be called — rather than
// RequestHandler/StreamRequestHandler, which read as siblings of IRequestHandler and
// IStreamRequestHandler while being something else entirely.

/// <summary>Continues a result-bearing request pipeline.</summary>
public delegate ValueTask<Result<TOut>> RequestPipelineNext<TOut>(CancellationToken ct);

/// <summary>Wraps execution of a matching no-result request handler.</summary>
public interface IRequestPipelineBehavior<in TRequest> where TRequest : IRequest
{
    /// <summary>Executes this behavior and, when appropriate, the remainder of the pipeline.</summary>
    ValueTask<Result> HandleAsync(IRequestContext<TRequest> context, RequestPipelineNext continuation,
        CancellationToken ct);
}

/// <summary>Wraps execution of a matching result-bearing request handler.</summary>
public interface IRequestPipelineBehavior<in TRequest, TOut> where TRequest : IRequest<TOut>
{
    /// <summary>Executes this behavior and, when appropriate, the remainder of the pipeline.</summary>
    ValueTask<Result<TOut>> HandleAsync(IRequestContext<TRequest> context, RequestPipelineNext<TOut> continuation,
        CancellationToken ct);
}
