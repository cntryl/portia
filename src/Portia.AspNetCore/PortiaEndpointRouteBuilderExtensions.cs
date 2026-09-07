using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cntryl.Portia;

/// <summary>
/// Maps a Portia request straight onto a minimal API endpoint, one method per HTTP verb. Only a
/// request marked <see cref="ICallable" /> can be mapped — the same opt-in every other remote
/// transport requires, since an HTTP endpoint is a remote caller like any other.
///
/// Route values, query string, and JSON body are combined into the request by
/// <c>RequestHttpBindingGenerator</c>, which intercepts each <c>MapPortia*</c> call site
/// (via <c>System.Runtime.CompilerServices.InterceptsLocationAttribute</c>) and inlines
/// binding code there — the request type itself never needs to reference ASP.NET Core, since the
/// generator resolves it purely from metadata. A primary-constructor parameter whose name matches
/// a <c>{token}</c> in the route pattern binds from the route; anything left over binds from the
/// JSON body on POST/PUT/PATCH, or from the query string on GET/DELETE.
///
/// There's no separate "async" method to opt into queue-backed dispatch: on POST/PUT/PATCH/DELETE,
/// the generator checks whether <c>TRequest</c> also implements <see cref="IQueuable" />. If it
/// does, a caller sending the standard <c>Prefer: respond-async</c> header (RFC 7240) gets the
/// request enqueued and a 202 Accepted back immediately; anyone else, and any request that isn't
/// <see cref="IQueuable" /> at all, gets the ordinary dispatch-and-wait behavior. The request
/// itself decides whether the pivot is available, the same way it opts into every other transport.
///
/// A result-bearing request restates its result type at the call site
/// (<c>MapPortiaPost&lt;CreateOrder, OrderId&gt;</c>) even though <c>IRequest&lt;OrderId&gt;</c>
/// already carries it. That is a C# limitation, not a design choice: type arguments are inferred
/// from method arguments, never from a generic constraint, so there is no way to write a
/// one-type-argument overload that recovers <c>TOut</c> from <c>TRequest</c>.
///
/// Consumer compilations must reference Portia.Generators. Mapping patterns must be compile-time
/// constants and request constructors must have supported binding shapes; unsupported mappings
/// receive compiler diagnostics. Generated body binding honors configured ASP.NET HTTP JSON options.
/// </summary>
public static class PortiaEndpointRouteBuilderExtensions
{
    /// <summary>Supplies explicit contextual route values for asynchronous HTTP dispatch.</summary>
    public static RouteHandlerBuilder WithPortiaRouteValues(this RouteHandlerBuilder builder, Func<HttpContext, RequestRouteValues> resolver)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resolver);
        return builder.WithMetadata(new PortiaHttpRouteValues(resolver));
    }

    /// <summary>
    /// Maps a no-result request to a GET endpoint dispatched through <see cref="IRequestBus" />.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    public static RouteHandlerBuilder MapPortiaGet<TRequest>(this IEndpointRouteBuilder app, string pattern)
        where TRequest : IRequest, ICallable =>
        app.MapGet(pattern, async (TRequest request, HttpContext httpContext, IRequestBus bus, CancellationToken ct) =>
            (await bus.DispatchAsync(request, PortiaHttpBinding.CreateDispatchContext(httpContext), ct)).ToHttpResult());

    /// <summary>
    /// Maps a request with a result to a GET endpoint dispatched through <see cref="IRequestBus" />.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    public static RouteHandlerBuilder MapPortiaGet<TRequest, TOut>(this IEndpointRouteBuilder app, string pattern)
        where TRequest : IRequest<TOut>, ICallable =>
        app.MapGet(pattern, async (TRequest request, HttpContext httpContext, IRequestBus bus, CancellationToken ct) =>
            (await bus.DispatchAsync(request, PortiaHttpBinding.CreateDispatchContext(httpContext), ct)).ToHttpResult());

    /// <summary>
    /// Maps a no-result request to a POST endpoint dispatched through <see cref="IRequestBus" />.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    public static RouteHandlerBuilder MapPortiaPost<TRequest>(this IEndpointRouteBuilder app, string pattern)
        where TRequest : IRequest, ICallable =>
        app.MapPost(pattern, async (TRequest request, HttpContext httpContext, IRequestBus bus, CancellationToken ct) =>
            (await bus.DispatchAsync(request, PortiaHttpBinding.CreateDispatchContext(httpContext), ct)).ToHttpResult());

    /// <summary>
    /// Maps a request with a result to a POST endpoint dispatched through <see cref="IRequestBus" />.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    public static RouteHandlerBuilder MapPortiaPost<TRequest, TOut>(this IEndpointRouteBuilder app, string pattern)
        where TRequest : IRequest<TOut>, ICallable =>
        app.MapPost(pattern, async (TRequest request, HttpContext httpContext, IRequestBus bus, CancellationToken ct) =>
            (await bus.DispatchAsync(request, PortiaHttpBinding.CreateDispatchContext(httpContext), ct)).ToHttpResult());

    /// <summary>
    /// Maps a no-result request to a PUT endpoint dispatched through <see cref="IRequestBus" />.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    public static RouteHandlerBuilder MapPortiaPut<TRequest>(this IEndpointRouteBuilder app, string pattern)
        where TRequest : IRequest, ICallable =>
        app.MapPut(pattern, async (TRequest request, HttpContext httpContext, IRequestBus bus, CancellationToken ct) =>
            (await bus.DispatchAsync(request, PortiaHttpBinding.CreateDispatchContext(httpContext), ct)).ToHttpResult());

    /// <summary>
    /// Maps a request with a result to a PUT endpoint dispatched through <see cref="IRequestBus" />.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    public static RouteHandlerBuilder MapPortiaPut<TRequest, TOut>(this IEndpointRouteBuilder app, string pattern)
        where TRequest : IRequest<TOut>, ICallable =>
        app.MapPut(pattern, async (TRequest request, HttpContext httpContext, IRequestBus bus, CancellationToken ct) =>
            (await bus.DispatchAsync(request, PortiaHttpBinding.CreateDispatchContext(httpContext), ct)).ToHttpResult());

    /// <summary>
    /// Maps a no-result request to a PATCH endpoint dispatched through <see cref="IRequestBus" />.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    public static RouteHandlerBuilder MapPortiaPatch<TRequest>(this IEndpointRouteBuilder app, string pattern)
        where TRequest : IRequest, ICallable =>
        app.MapPatch(pattern, async (TRequest request, HttpContext httpContext, IRequestBus bus, CancellationToken ct) =>
            (await bus.DispatchAsync(request, PortiaHttpBinding.CreateDispatchContext(httpContext), ct)).ToHttpResult());

    /// <summary>
    /// Maps a request with a result to a PATCH endpoint dispatched through <see cref="IRequestBus" />.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    public static RouteHandlerBuilder MapPortiaPatch<TRequest, TOut>(this IEndpointRouteBuilder app, string pattern)
        where TRequest : IRequest<TOut>, ICallable =>
        app.MapPatch(pattern, async (TRequest request, HttpContext httpContext, IRequestBus bus, CancellationToken ct) =>
            (await bus.DispatchAsync(request, PortiaHttpBinding.CreateDispatchContext(httpContext), ct)).ToHttpResult());

    /// <summary>
    /// Maps a no-result request to a DELETE endpoint dispatched through <see cref="IRequestBus" />.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    public static RouteHandlerBuilder MapPortiaDelete<TRequest>(this IEndpointRouteBuilder app, string pattern)
        where TRequest : IRequest, ICallable =>
        app.MapDelete(pattern, async (TRequest request, HttpContext httpContext, IRequestBus bus, CancellationToken ct) =>
            (await bus.DispatchAsync(request, PortiaHttpBinding.CreateDispatchContext(httpContext), ct)).ToHttpResult());

    /// <summary>
    /// Maps a request with a result to a DELETE endpoint dispatched through <see cref="IRequestBus" />.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    public static RouteHandlerBuilder MapPortiaDelete<TRequest, TOut>(this IEndpointRouteBuilder app, string pattern)
        where TRequest : IRequest<TOut>, ICallable =>
        app.MapDelete(pattern, async (TRequest request, HttpContext httpContext, IRequestBus bus, CancellationToken ct) =>
            (await bus.DispatchAsync(request, PortiaHttpBinding.CreateDispatchContext(httpContext), ct)).ToHttpResult());

    /// <summary>
    /// Maps a request that produces a sequence of results to a GET endpoint, streamed to the
    /// caller as one incrementally-flushed JSON array via <see cref="RequestBusExtensions.StreamAsync{TOut}(IRequestBus, IStreamRequest{TOut}, System.Security.Claims.ClaimsPrincipal, CancellationToken)" />
    /// — items are written as they're produced, never buffered into memory first.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TOut">The type of each item produced.</typeparam>
    public static RouteHandlerBuilder MapPortiaGetStream<TRequest, TOut>(this IEndpointRouteBuilder app, string pattern)
        where TRequest : IStreamRequest<TOut>, ICallable =>
        app.MapGet(pattern, (TRequest request, HttpContext httpContext, IRequestBus bus, CancellationToken ct) => PortiaStreamResults.Json(bus.DispatchStreamAsync(request, PortiaHttpBinding.CreateDispatchContext(httpContext), ct)));

    /// <summary>
    /// Maps a request that produces a sequence of results to a GET endpoint, streamed to the
    /// caller as Server-Sent Events. Reuses the exact same <see cref="RequestBusExtensions.StreamAsync{TOut}(IRequestBus, IStreamRequest{TOut}, System.Security.Claims.ClaimsPrincipal, CancellationToken)" />
    /// source as <see cref="MapPortiaGetStream{TRequest, TOut}" /> — pick this one instead when the
    /// caller wants a long-lived SSE connection (e.g. a browser <c>EventSource</c>) rather than a
    /// single streamed JSON array.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TOut">The type of each item produced.</typeparam>
    public static RouteHandlerBuilder MapPortiaGetSse<TRequest, TOut>(this IEndpointRouteBuilder app, string pattern)
        where TRequest : IStreamRequest<TOut>, ICallable =>
        app.MapGet(pattern, (TRequest request, HttpContext httpContext, IRequestBus bus, CancellationToken ct) =>
            PortiaStreamResults.Sse(bus.DispatchStreamAsync(request, PortiaHttpBinding.CreateDispatchContext(httpContext), ct)));

}

sealed record PortiaHttpRouteValues(Func<HttpContext, RequestRouteValues> Resolve);
