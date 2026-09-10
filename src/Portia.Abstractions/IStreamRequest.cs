namespace Cntryl.Portia;

/// <summary>
///     Marks a request dispatched through the request bus that produces a sequence of results,
///     streamed as they become available rather than buffered into a single response. Distinct from
///     <see cref="IRequest{TOut}" />: a stream has no single outcome to wrap in a <see cref="Result{T}" />
///     — a mid-stream failure ends the stream with an exception instead, same as any other
///     <see cref="IAsyncEnumerable{T}" /> producer.
/// </summary>
/// <typeparam name="TOut">The type of each item produced.</typeparam>
public interface IStreamRequest<TOut> : IRequestBase;
