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
    /// <summary>Dispatches a new child request, inheriting actor and correlation from an explicit parent.</summary>
    ValueTask<Result> SendAsync(IRequest request, IExecutionContext parent, CancellationToken ct = default)
        => DispatchAsync(request, new RequestDispatchContext(parent.Actor, metadata: RequestMetadata.FromParent(parent)), ct);

    /// <summary>Dispatches a new result-bearing child request with explicit causal inheritance.</summary>
    ValueTask<Result<TOut>> SendAsync<TOut>(IRequest<TOut> request, IExecutionContext parent, CancellationToken ct = default)
        => DispatchAsync(request, new RequestDispatchContext(parent.Actor, metadata: RequestMetadata.FromParent(parent)), ct);

    /// <summary>Streams a new child request with explicit causal inheritance.</summary>
    IAsyncEnumerable<TOut> StreamAsync<TOut>(IStreamRequest<TOut> request, IExecutionContext parent, CancellationToken ct = default)
        => DispatchStreamAsync(request, new RequestDispatchContext(parent.Actor, metadata: RequestMetadata.FromParent(parent)), ct);

    /// <summary>Dispatches receiver-created execution state. </summary>
    ValueTask<Result> DispatchAsync(IRequest request, RequestDispatchContext context, CancellationToken ct = default);

    /// <summary>Dispatches a result-bearing request using receiver-created execution state.</summary>
    ValueTask<Result<TOut>> DispatchAsync<TOut>(IRequest<TOut> request, RequestDispatchContext context, CancellationToken ct = default);

    /// <summary>Streams a request using receiver-created execution state.</summary>
    IAsyncEnumerable<TOut> DispatchStreamAsync<TOut>(IStreamRequest<TOut> request, RequestDispatchContext context, CancellationToken ct = default);
}
