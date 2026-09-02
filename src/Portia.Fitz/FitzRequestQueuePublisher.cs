using Cntryl.Fitz.Abstractions.Domains.Queue;

namespace Cntryl.Portia;

/// <summary>
/// Enqueues no-result requests onto a Fitz queue for later dispatch.
/// </summary>
/// <param name="queue">The Fitz queue client.</param>
/// <param name="serializer">The request serializer.</param>
public sealed class FitzRequestQueuePublisher(IQueueClient queue, IRequestSerializer serializer) : IRequestQueuePublisher
{
    readonly IQueueClient _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    readonly IRequestSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

    /// <inheritdoc />
    public async ValueTask EnqueueAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken, CancellationToken ct = default)
        where TRequest : IRequest, IQueuable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(routeValues);

        var route = FitzRouting.ResolveQueueRoute(request, routeValues);
        var body = _serializer.Serialize(request, actorToken);
        _ = await _queue.EnqueueAsync(route, body, null, ct).ConfigureAwait(false);
    }
}
