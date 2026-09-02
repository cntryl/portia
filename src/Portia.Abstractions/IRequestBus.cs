using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
/// Dispatches requests to their single registered handler, independent of where the request
/// originated (an HTTP call, a queue message, an RPC call, or anywhere else). Every dispatch
/// runs each request's declared authorization (<see cref="RequiresPermissionAttribute" /> and/or
/// <see cref="IRequestAuthorizer{TRequest}" />) against the given actor before its handler runs
/// — since every transport funnels through here, that check is implemented exactly once and
/// applies uniformly everywhere, not per-transport.
/// </summary>
public interface IRequestBus
{
    /// <summary>
    /// Dispatches a request that produces no result to its handler.
    /// </summary>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="actor">The actor making the request. Never inferred ambiently — an
    /// unauthenticated or internal caller should pass <see cref="RequestActor.Anonymous" /> or
    /// <see cref="RequestActor.System" /> explicitly, not a null or a forgotten default.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of handling the request.</returns>
    ValueTask<Result> SendAsync(IRequest request, ClaimsPrincipal actor, CancellationToken ct = default);

    /// <summary>
    /// Dispatches a request that produces a result to its handler.
    /// </summary>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="actor">The actor making the request. Never inferred ambiently — an
    /// unauthenticated or internal caller should pass <see cref="RequestActor.Anonymous" /> or
    /// <see cref="RequestActor.System" /> explicitly, not a null or a forgotten default.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of handling the request.</returns>
    ValueTask<Result<TOut>> SendAsync<TOut>(IRequest<TOut> request, ClaimsPrincipal actor, CancellationToken ct = default);

    /// <summary>
    /// Dispatches a request that produces a sequence of results to its handler.
    /// </summary>
    /// <typeparam name="TOut">The type of each item produced.</typeparam>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="actor">The actor making the request. Never inferred ambiently — an
    /// unauthenticated or internal caller should pass <see cref="RequestActor.Anonymous" /> or
    /// <see cref="RequestActor.System" /> explicitly, not a null or a forgotten default.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The items produced by handling the request, streamed as they become available.</returns>
    IAsyncEnumerable<TOut> StreamAsync<TOut>(IStreamRequest<TOut> request, ClaimsPrincipal actor, CancellationToken ct = default);
}
