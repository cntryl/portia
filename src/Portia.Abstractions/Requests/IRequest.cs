namespace Cntryl.Portia;

/// <summary>
///     Marks a request dispatched through the request bus that produces no result.
/// </summary>
public interface IRequest : IRequestBase;

/// <summary>
///     Marks a request dispatched through the request bus that produces a result.
/// </summary>
/// <typeparam name="TOut">The type of the result produced by handling the request.</typeparam>
public interface IRequest<TOut> : IRequestBase;
