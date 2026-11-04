namespace Cntryl.Portia;

/// <summary>
///     Enqueues a no-result request for later dispatch through the request bus, independent of
///     which queue technology (Fitz, a message broker, or anything else) carries it.
/// </summary>
public interface IRequestQueuePublisher
{
    /// <summary>
    ///     Enqueues a request. Only a request marked <see cref="IQueuable" /> can be enqueued.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="request">The request to enqueue.</param>
    /// <param name="routeValues">
    ///     Values for any route segment the request's
    ///     <see cref="RequestRouteAttribute" /> left as <see cref="RequestRouteAttribute.Wildcard" />.
    /// </param>
    /// <param name="actorToken">
    ///     The enqueuing actor's raw bearer token, or <see langword="null" />
    ///     for an unauthenticated actor. Carried with the request and re-validated — expiry included
    ///     — at the moment it's actually dequeued, which may be long after it was enqueued.
    /// </param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the enqueue.</returns>
    ValueTask EnqueueAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
        CancellationToken ct = default)
        where TRequest : IRequest, IQueuable
        => EnqueueAsync(request, routeValues, actorToken, RequestMetadata.FromRequest(request), ct);

    /// <summary>Transmits a request with explicit logical identity and separately supplied credentials.</summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="request">The request to enqueue.</param>
    /// <param name="routeValues">
    ///     Values for any route segment the request's
    ///     <see cref="RequestRouteAttribute" /> left as <see cref="RequestRouteAttribute.Wildcard" />.
    /// </param>
    /// <param name="actorToken">
    ///     The enqueuing actor's raw bearer token, or <see langword="null" />
    ///     for an unauthenticated actor.
    /// </param>
    /// <param name="metadata">The logical request identity to propagate.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the enqueue.</returns>
    ValueTask EnqueueAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
        RequestMetadata metadata, CancellationToken ct = default)
        where TRequest : IRequest, IQueuable;

    /// <summary>Enqueues one request with an explicit initial visibility delay or message lifetime.</summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="request">The request to enqueue.</param>
    /// <param name="routeValues">Values for wildcard route segments.</param>
    /// <param name="actorToken">The actor credential to validate when the request is delivered.</param>
    /// <param name="options">Publication delay and lifetime; unsupported semantics fail explicitly.</param>
    /// <param name="ct">Cancels publication.</param>
    /// <returns>A task representing provider acceptance, subject to its durability guarantees.</returns>
    ValueTask EnqueueWithOptionsAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
        RequestQueueOptions options, CancellationToken ct)
        where TRequest : IRequest, IQueuable
        => EnqueueWithOptionsAsync(request, routeValues, actorToken, RequestMetadata.FromRequest(request), options, ct);

    /// <summary>Enqueues a request with explicit logical identity and publication controls.</summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="request">The request to enqueue.</param>
    /// <param name="routeValues">Values for wildcard route segments.</param>
    /// <param name="actorToken">The actor credential to validate when the request is delivered.</param>
    /// <param name="metadata">The logical request identity to propagate.</param>
    /// <param name="options">Publication delay and lifetime; unsupported semantics fail explicitly.</param>
    /// <param name="ct">Cancels publication.</param>
    /// <returns>A task representing provider acceptance, subject to its durability guarantees.</returns>
    ValueTask EnqueueWithOptionsAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
        RequestMetadata metadata, RequestQueueOptions options, CancellationToken ct = default)
        where TRequest : IRequest, IQueuable
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ct.ThrowIfCancellationRequested();
        if (options.TtlHours is not null || options.Delay is { } delay && delay != TimeSpan.Zero)
            throw new NotSupportedException("This queue publisher does not support publication delay or message lifetime.");
        return EnqueueAsync(request, routeValues, actorToken, metadata, ct);
    }
}
