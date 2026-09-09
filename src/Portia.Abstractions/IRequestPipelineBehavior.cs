namespace Cntryl.Portia;

/// <summary>Continues a no-result request pipeline.</summary>
public delegate ValueTask<Result> RequestHandler(CancellationToken ct);

/// <summary>Continues a result-bearing request pipeline.</summary>
public delegate ValueTask<Result<TOut>> RequestHandler<TOut>(CancellationToken ct);

/// <summary>Continues a streaming request pipeline.</summary>
public delegate IAsyncEnumerable<TOut> StreamRequestHandler<TOut>(CancellationToken ct);

/// <summary>Wraps execution of a matching no-result request handler.</summary>
public interface IRequestPipelineBehavior<in TRequest> where TRequest : IRequest
{
    /// <summary>Executes this behavior and, when appropriate, the remainder of the pipeline.</summary>
    ValueTask<Result> HandleAsync(IRequestContext<TRequest> context, RequestHandler nextHandler, CancellationToken ct);
}

/// <summary>Wraps execution of a matching result-bearing request handler.</summary>
public interface IRequestPipelineBehavior<in TRequest, TOut> where TRequest : IRequest<TOut>
{
    /// <summary>Executes this behavior and, when appropriate, the remainder of the pipeline.</summary>
    ValueTask<Result<TOut>> HandleAsync(IRequestContext<TRequest> context, RequestHandler<TOut> nextHandler, CancellationToken ct);
}

/// <summary>Wraps enumeration of a matching streaming request handler.</summary>
public interface IStreamRequestPipelineBehavior<in TRequest, TOut> where TRequest : IStreamRequest<TOut>
{
    /// <summary>Returns a stream that wraps the remainder of the pipeline.</summary>
    IAsyncEnumerable<TOut> HandleAsync(IRequestContext<TRequest> context, StreamRequestHandler<TOut> nextHandler, CancellationToken ct);
}
