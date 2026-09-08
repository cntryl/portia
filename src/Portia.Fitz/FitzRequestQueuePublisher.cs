using Cntryl.Fitz.Abstractions.Domains.Queue;

namespace Cntryl.Portia;

/// <summary>
/// Enqueues no-result requests onto a Fitz queue for later dispatch.
/// </summary>
/// <param name="queue">The Fitz queue client.</param>
/// <param name="serializer">The request serializer.</param>
/// <param name="catalog">Provides generated request routes.</param>
public sealed class FitzRequestQueuePublisher(IQueueClient queue, IRequestSerializer serializer, RequestTransportCatalog? catalog = null) : IRequestQueuePublisher
{
    readonly IQueueClient _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    readonly IRequestSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    readonly RequestTransportCatalog _catalog = catalog ?? (serializer as JsonRequestSerializer)?.Catalog
        ?? throw new ArgumentNullException(nameof(catalog));

    /// <inheritdoc />
    public ValueTask EnqueueAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken, CancellationToken ct = default)
        where TRequest : IRequest, IQueuable
        => EnqueueAsync(request, routeValues, actorToken, RequestMetadata.Create(), ct);

    /// <inheritdoc />
    public async ValueTask EnqueueAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
        where TRequest : IRequest, IQueuable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(routeValues);

        using var activity = PortiaTelemetry.StartSend(typeof(TRequest).Name, "fitz.queue");
        var started = PortiaTelemetry.StartTimestamp();
        var outcome = "success";
        try
        {
            var route = FitzRouting.ResolveQueueRoute(_catalog, request, routeValues);
            var body = _serializer.Serialize(request, actorToken, metadata, PortiaTelemetry.CaptureTraceContext());
            _ = await _queue.EnqueueAsync(route, body, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { outcome = "canceled"; throw; }
        catch { outcome = "fault"; throw; }
        finally { PortiaTelemetry.TransportFinished(started, "fitz.queue", "enqueue", outcome); }
    }
}
