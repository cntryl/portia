using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
/// The ergonomic way to dispatch a request. These build on <see cref="IRequestBus" />'s dispatch
/// primitives and its <see cref="IRequestBus.CreateContext" />, so every implementation gets the
/// same behaviour — including its own clock — instead of each reimplementing these six overloads.
/// </summary>
public static class RequestBusExtensions
{
    /// <summary>Dispatches a request that produces no result to its handler.</summary>
    /// <param name="bus">The bus to dispatch through.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="actor">The actor making the request. Never inferred ambiently — an
    /// unauthenticated or internal caller should pass <see cref="RequestActor.Anonymous" /> or
    /// <see cref="RequestActor.System" /> explicitly, not a null or a forgotten default.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of handling the request.</returns>
    public static ValueTask<Result> SendAsync(this IRequestBus bus, IRequest request, ClaimsPrincipal actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bus);
        return bus.DispatchAsync(request, bus.CreateContext(actor), ct);
    }

    /// <summary>Dispatches a request that produces a result to its handler.</summary>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    /// <param name="bus">The bus to dispatch through.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="actor">The actor making the request.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of handling the request.</returns>
    public static ValueTask<Result<TOut>> SendAsync<TOut>(this IRequestBus bus, IRequest<TOut> request, ClaimsPrincipal actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bus);
        return bus.DispatchAsync(request, bus.CreateContext(actor), ct);
    }

    /// <summary>Dispatches a request that produces a sequence of results to its handler.</summary>
    /// <typeparam name="TOut">The type of each item produced.</typeparam>
    /// <param name="bus">The bus to dispatch through.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="actor">The actor making the request.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The items produced, streamed as they become available.</returns>
    public static IAsyncEnumerable<TOut> StreamAsync<TOut>(this IRequestBus bus, IStreamRequest<TOut> request, ClaimsPrincipal actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bus);
        return bus.DispatchStreamAsync(request, bus.CreateContext(actor), ct);
    }

    /// <summary>Dispatches a new child request, inheriting actor and correlation from an explicit parent.</summary>
    /// <param name="bus">The bus to dispatch through.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="parent">The execution causing this request.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of handling the request.</returns>
    public static ValueTask<Result> SendAsync(this IRequestBus bus, IRequest request, IExecutionContext parent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(parent);
        return bus.DispatchAsync(request, bus.CreateContext(parent.Actor, RequestMetadata.FromParent(parent)), ct);
    }

    /// <summary>Dispatches a new result-bearing child request with explicit causal inheritance.</summary>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    /// <param name="bus">The bus to dispatch through.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="parent">The execution causing this request.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of handling the request.</returns>
    public static ValueTask<Result<TOut>> SendAsync<TOut>(this IRequestBus bus, IRequest<TOut> request, IExecutionContext parent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(parent);
        return bus.DispatchAsync(request, bus.CreateContext(parent.Actor, RequestMetadata.FromParent(parent)), ct);
    }

    /// <summary>Streams a new child request with explicit causal inheritance.</summary>
    /// <typeparam name="TOut">The type of each item produced.</typeparam>
    /// <param name="bus">The bus to dispatch through.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="parent">The execution causing this request.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The items produced, streamed as they become available.</returns>
    public static IAsyncEnumerable<TOut> StreamAsync<TOut>(this IRequestBus bus, IStreamRequest<TOut> request, IExecutionContext parent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(parent);
        return bus.DispatchStreamAsync(request, bus.CreateContext(parent.Actor, RequestMetadata.FromParent(parent)), ct);
    }
}
