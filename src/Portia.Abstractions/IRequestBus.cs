using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
///     Dispatches requests to their single registered handler, independent of where the request
///     originated (an HTTP call, a queue message, an RPC call, or anywhere else). Every dispatch
///     runs each request's declared authorization (<see cref="RequiresPermissionAttribute" /> and/or
///     <see cref="IRequestAuthorizer{TRequest}" />) against the given actor before its handler runs
///     — since every transport funnels through here, that check is implemented exactly once and
///     applies uniformly everywhere, not per-transport.
///     The interface is deliberately small: three dispatch primitives plus the context factory they
///     need. The ergonomic <c>SendAsync</c>/<c>StreamAsync</c> overloads most callers use live in
///     <see cref="RequestBusExtensions" />, so an alternative implementation has four members to
///     write rather than nine, and cannot accidentally diverge from them.
/// </summary>
public interface IRequestBus
{
    /// <summary>
    ///     Creates the execution state one dispatch runs under. This is the seam the
    ///     <c>SendAsync</c>/<c>StreamAsync</c> convenience methods build on, so every implementation
    ///     supplies its own clock and identity generation rather than each caller reinventing them.
    /// </summary>
    /// <param name="actor">
    ///     The actor making the request. Never inferred ambiently — an
    ///     unauthenticated or internal caller should pass <see cref="RequestActor.Anonymous" /> or
    ///     <see cref="RequestActor.System" /> explicitly, not a null or a forgotten default.
    /// </param>
    /// <param name="metadata">Logical identity to propagate, or null to start a new request.</param>
    /// <returns>Receiver-created execution state.</returns>
    RequestDispatchContext CreateContext(ClaimsPrincipal actor, RequestMetadata? metadata = null);

    /// <summary>Dispatches a no-result request using receiver-created execution state.</summary>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="context">Execution state from <see cref="CreateContext" />.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of handling the request.</returns>
    ValueTask<Result> DispatchAsync(IRequest request, RequestDispatchContext context, CancellationToken ct = default);

    /// <summary>Dispatches a result-bearing request using receiver-created execution state.</summary>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="context">Execution state from <see cref="CreateContext" />.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of handling the request.</returns>
    ValueTask<Result<TOut>> DispatchAsync<TOut>(IRequest<TOut> request, RequestDispatchContext context,
        CancellationToken ct = default);

    /// <summary>Streams a request using receiver-created execution state.</summary>
    /// <typeparam name="TOut">The type of the values produced.</typeparam>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="context">Execution state from <see cref="CreateContext" />.</param>
    /// <param name="ct">A token that can cancel enumeration.</param>
    /// <returns>The values produced by the handler, yielded as they are produced.</returns>
    IAsyncEnumerable<TOut> DispatchStreamAsync<TOut>(IStreamRequest<TOut> request, RequestDispatchContext context,
        CancellationToken ct = default);
}
