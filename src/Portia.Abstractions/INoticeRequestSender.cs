namespace Cntryl.Portia;

/// <summary>
/// Publishes a no-result request over live (ephemeral) fanout, independent of which
/// notification technology (Fitz, or anything else) carries it. There is no queueing or
/// redelivery — a request published while nothing is subscribed is lost.
/// </summary>
public interface INoticeRequestSender
{
    /// <summary>
    /// Publishes a request. Only a request marked <see cref="INotifiable" /> can be published
    /// this way.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="request">The request to publish.</param>
    /// <param name="routeValues">Values for any route segment the request's
    /// <see cref="RequestRouteAttribute" /> left as <see cref="RequestRouteAttribute.Wildcard" />.</param>
    /// <param name="actorToken">The publishing actor's raw bearer token, or <see langword="null" />
    /// for an unauthenticated actor — re-validated on the receiving side, not just forwarded as-is.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the publish.</returns>
    ValueTask PublishAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken, CancellationToken ct = default)
        where TRequest : IRequest, INotifiable;
}
