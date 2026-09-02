namespace Cntryl.Portia;

/// <summary>
/// Common marker shared by <see cref="IRequest" /> and <see cref="IRequest{TOut}" />, so
/// infrastructure that must handle either kind (serialization, routing) has a type to work with.
/// Request and handler authors should implement <see cref="IRequest" /> or
/// <see cref="IRequest{TOut}" /> directly, never this marker.
/// </summary>
public interface IRequestBase;

/// <summary>
/// Marks a request dispatched through the request bus that produces no result.
/// </summary>
public interface IRequest : IRequestBase;

/// <summary>
/// Marks a request dispatched through the request bus that produces a result.
/// </summary>
/// <typeparam name="TOut">The type of the result produced by handling the request.</typeparam>
public interface IRequest<TOut> : IRequestBase;
