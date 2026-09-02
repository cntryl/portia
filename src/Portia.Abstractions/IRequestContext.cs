namespace Cntryl.Portia;

/// <summary>
/// Carries the request into a request handler.
/// </summary>
/// <typeparam name="TRequest">The concrete request type.</typeparam>
public interface IRequestContext<out TRequest>
{
    /// <summary>
    /// Gets the request being handled.
    /// </summary>
    TRequest Request { get; }
}

/// <summary>
/// The default <see cref="IRequestContext{TRequest}" /> implementation, constructed by
/// generated request-bus dispatch code.
/// </summary>
/// <typeparam name="TRequest">The concrete request type.</typeparam>
/// <param name="request">The request being handled.</param>
public sealed class RequestContext<TRequest>(TRequest request) : IRequestContext<TRequest>
{
    /// <inheritdoc />
    public TRequest Request { get; } = request;
}
