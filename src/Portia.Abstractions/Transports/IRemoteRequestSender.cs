namespace Cntryl.Portia;

/// <summary>
///     Sends a request to an out-of-process handler and awaits its result, independent of which
///     RPC technology (Fitz, gRPC, or anything else) carries it. A request's handler is unaware of
///     whether it was reached this way, through <see cref="IRequestBus" /> in-process, or through a
///     queue — the same <see cref="IRequestHandler{TRequest}" /> runs regardless.
/// </summary>
public interface IRemoteRequestSender
{
#pragma warning disable RS0026 // Metadata overloads intentionally mirror the established sender API.
    /// <summary>Sends a no-result request and reports whether it completed, was dispatched, or ended uncertainly.</summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="request">The request to send.</param>
    /// <param name="routeValues">Values for wildcard route segments.</param>
    /// <param name="actorToken">The caller's bearer token, or <see langword="null" />.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The completed result or a transport-aware cancellation outcome.</returns>
    ValueTask<RemoteRequestOutcome> SendWithOutcomeAsync<TRequest>(TRequest request,
        RequestRouteValues routeValues, string? actorToken, CancellationToken ct = default)
        where TRequest : IRequest, ICallable
        => SendWithOutcomeAsync(request, routeValues, actorToken, RequestMetadata.Create(), ct);

    /// <summary>Sends a no-result request with explicit logical identity and reports its transport outcome.</summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="request">The request to send.</param>
    /// <param name="routeValues">Values for wildcard route segments.</param>
    /// <param name="actorToken">The caller's bearer token, or <see langword="null" />.</param>
    /// <param name="metadata">The logical request identity to propagate.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The completed result or a transport-aware cancellation outcome.</returns>
    async ValueTask<RemoteRequestOutcome> SendWithOutcomeAsync<TRequest>(TRequest request,
        RequestRouteValues routeValues, string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
        where TRequest : IRequest, ICallable
    {
        if (ct.IsCancellationRequested)
            return new RemoteRequestOutcome.NotDispatched();
        try
        {
            return new RemoteRequestOutcome.Completed(await SendAsync(request, routeValues, actorToken, metadata, ct)
                .ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Existing implementations cannot report whether dispatch happened before cancellation.
            return new RemoteRequestOutcome.OutcomeUnknown();
        }
    }

    /// <summary>Sends a result-bearing request and reports whether it completed, was dispatched, or ended uncertainly.</summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TOut">The value returned when the request completes.</typeparam>
    /// <param name="request">The request to send.</param>
    /// <param name="routeValues">Values for wildcard route segments.</param>
    /// <param name="actorToken">The caller's bearer token, or <see langword="null" />.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The completed result or a transport-aware cancellation outcome.</returns>
    ValueTask<RemoteRequestOutcome<TOut>> SendWithOutcomeAsync<TRequest, TOut>(TRequest request,
        RequestRouteValues routeValues, string? actorToken, CancellationToken ct = default)
        where TRequest : IRequest<TOut>, ICallable
        => SendWithOutcomeAsync<TRequest, TOut>(request, routeValues, actorToken, RequestMetadata.Create(), ct);

    /// <summary>Sends a result-bearing request with explicit logical identity and reports its transport outcome.</summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TOut">The value returned when the request completes.</typeparam>
    /// <param name="request">The request to send.</param>
    /// <param name="routeValues">Values for wildcard route segments.</param>
    /// <param name="actorToken">The caller's bearer token, or <see langword="null" />.</param>
    /// <param name="metadata">The logical request identity to propagate.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The completed result or a transport-aware cancellation outcome.</returns>
    async ValueTask<RemoteRequestOutcome<TOut>> SendWithOutcomeAsync<TRequest, TOut>(TRequest request,
        RequestRouteValues routeValues, string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
        where TRequest : IRequest<TOut>, ICallable
    {
        if (ct.IsCancellationRequested)
            return new RemoteRequestOutcome<TOut>.NotDispatched();
        try
        {
            return new RemoteRequestOutcome<TOut>.Completed(await SendAsync<TRequest, TOut>(request, routeValues,
                actorToken, metadata, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Existing implementations cannot report whether dispatch happened before cancellation.
            return new RemoteRequestOutcome<TOut>.OutcomeUnknown();
        }
    }
#pragma warning restore RS0026

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
    /// <param name="metadata">The logical request identity to propagate.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of handling the request.</returns>
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
    /// <param name="metadata">The logical request identity to propagate.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of handling the request.</returns>
    ValueTask<Result<TOut>> SendAsync<TRequest, TOut>(TRequest request, RequestRouteValues routeValues,
        string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
        where TRequest : IRequest<TOut>, ICallable;
}
