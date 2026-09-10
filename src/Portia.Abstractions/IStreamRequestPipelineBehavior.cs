namespace Cntryl.Portia;

/// <summary>Wraps enumeration of a matching streaming request handler.</summary>
public interface IStreamRequestPipelineBehavior<in TRequest, TOut> where TRequest : IStreamRequest<TOut>
{
    /// <summary>Returns a stream that wraps the remainder of the pipeline.</summary>
    IAsyncEnumerable<TOut> HandleAsync(IRequestContext<TRequest> context, StreamRequestPipelineNext<TOut> continuation,
        CancellationToken ct);
}
