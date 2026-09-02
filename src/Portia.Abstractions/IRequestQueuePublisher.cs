namespace Cntryl.Portia;

/// <summary>
/// Enqueues a no-result request for later dispatch through the request bus, independent of
/// which queue technology (Fitz, a message broker, or anything else) carries it.
/// </summary>
public interface IRequestQueuePublisher
{
    /// <summary>
    /// Enqueues a request. Only a request marked <see cref="IQueuable" /> can be enqueued.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="request">The request to enqueue.</param>
    /// <param name="routeValues">Values for any route segment the request's
    /// <see cref="RequestRouteAttribute" /> left as <see cref="RequestRouteAttribute.Wildcard" />.</param>
    /// <param name="actorToken">The enqueuing actor's raw bearer token, or <see langword="null" />
    /// for an unauthenticated actor. Carried with the request and re-validated — expiry included
    /// — at the moment it's actually dequeued, which may be long after it was enqueued.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the enqueue.</returns>
    ValueTask EnqueueAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken, CancellationToken ct = default)
        where TRequest : IRequest, IQueuable;
}
