namespace Cntryl.Portia;

/// <summary>
///     Common marker shared by <see cref="IRequest" /> and <see cref="IRequest{TOut}" />, so
///     infrastructure that must handle either kind (serialization, routing) has a type to work with.
///     Request and handler authors should implement <see cref="IRequest" /> or
///     <see cref="IRequest{TOut}" /> directly, never this marker.
/// </summary>
public interface IRequestBase;
