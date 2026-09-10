namespace Cntryl.Portia;

/// <summary>
///     Sends a request to an out-of-process handler and awaits its result, independent of which
///     RPC technology (Fitz, gRPC, or anything else) carries it. A request's handler is unaware of
///     whether it was reached this way, through <see cref="IRequestBus" /> in-process, or through a
///     queue — the same <see cref="IRequestHandler{TRequest}" /> runs regardless.
/// </summary>
public interface IRemoteRequestSender
{
    /// <summary>
    ///     Sends a request that produces no result. Only a request marked <see cref="ICallable" />
    ///     can be sent this way.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="request">The request to send.</param>
    /// <param name="routeValues">
    ///     Values for any route segment the request's
    ///     <see cref="RequestRouteAttribute" /> left as <see cref="RequestRouteAttribute.Wildcard" />.
    /// </param>
    /// <param name="actorToken">
    ///     The caller's raw bearer token, or <see langword="null" /> for an
    ///     unauthenticated actor — re-validated on the receiving side, not just forwarded as-is.
    /// </param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of handling the request.</returns>
    ValueTask<Result> SendAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
        CancellationToken ct = default)
        where TRequest : IRequest, ICallable
        => SendAsync(request, routeValues, actorToken, RequestMetadata.Create(), ct);

    /// <summary>Transmits a request with explicit logical identity and separately supplied credentials.</summary>
    ValueTask<Result> SendAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
        RequestMetadata metadata, CancellationToken ct = default)
        where TRequest : IRequest, ICallable;

    /// <summary>
    ///     Sends a request that produces a result. Only a request marked <see cref="ICallable" />
    ///     can be sent this way.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    /// <param name="request">The request to send.</param>
    /// <param name="routeValues">
    ///     Values for any route segment the request's
    ///     <see cref="RequestRouteAttribute" /> left as <see cref="RequestRouteAttribute.Wildcard" />.
    /// </param>
    /// <param name="actorToken">
    ///     The caller's raw bearer token, or <see langword="null" /> for an
    ///     unauthenticated actor — re-validated on the receiving side, not just forwarded as-is.
    /// </param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of handling the request.</returns>
    ValueTask<Result<TOut>> SendAsync<TRequest, TOut>(TRequest request, RequestRouteValues routeValues,
        string? actorToken, CancellationToken ct = default)
        where TRequest : IRequest<TOut>, ICallable
        => SendAsync<TRequest, TOut>(request, routeValues, actorToken, RequestMetadata.Create(), ct);

    /// <summary>Transmits a request with explicit logical identity and separately supplied credentials.</summary>
    ValueTask<Result<TOut>> SendAsync<TRequest, TOut>(TRequest request, RequestRouteValues routeValues,
        string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
        where TRequest : IRequest<TOut>, ICallable;
}
