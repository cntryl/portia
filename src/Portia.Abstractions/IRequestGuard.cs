namespace Cntryl.Portia;

/// <summary>
///     Checks asynchronous surrounding state immediately before a matching unary request handler
///     runs. <typeparamref name="TRequest" /> may be a concrete request, a request-family
///     interface, or <see cref="IRequestBase" />. Guards are not authoritative domain invariants:
///     handlers and aggregates must still enforce decisions whose inputs can become stale.
/// </summary>
/// <typeparam name="TRequest">The concrete request or request-family interface checked.</typeparam>
public interface IRequestGuard<in TRequest>
    where TRequest : IRequestBase
{
    /// <summary>Checks whether the request may proceed to its handler.</summary>
    /// <param name="context">The request and execution context.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>Success to continue, or an expected failure to short-circuit handling.</returns>
    ValueTask<Result> GuardAsync(IRequestContext<TRequest> context, CancellationToken ct);
}
