namespace Cntryl.Portia;

/// <summary>
///     Enqueues no-result requests onto a Fitz queue for later dispatch.
/// </summary>
/// <param name="queue">The Fitz queue client.</param>
/// <param name="serializer">The request serializer.</param>
/// <param name="catalog">Provides generated request routes.</param>
public sealed class FitzRequestQueuePublisher(
    IQueueClient queue,
    IRequestSerializer serializer,
    RequestTransportCatalog? catalog = null) : IRequestQueuePublisher
{
    readonly RequestTransportCatalog _catalog = catalog ?? (serializer as JsonRequestSerializer)?.Catalog
        ?? throw new ArgumentNullException(nameof(catalog));

    readonly IQueueClient _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    readonly IRequestSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

    /// <inheritdoc />
    public ValueTask EnqueueAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
        CancellationToken ct = default)
        where TRequest : IRequest, IQueuable
        => EnqueueAsync(request, routeValues, actorToken, RequestMetadata.FromRequest(request), ct);

    /// <inheritdoc />
    public ValueTask EnqueueAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
        RequestMetadata metadata, CancellationToken ct = default)
        where TRequest : IRequest, IQueuable
        => EnqueueCoreAsync(request, routeValues, actorToken, metadata, null, ct);

    /// <inheritdoc />
    public ValueTask EnqueueWithOptionsAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
        RequestQueueOptions options, CancellationToken ct)
        where TRequest : IRequest, IQueuable
        => EnqueueWithOptionsAsync(request, routeValues, actorToken, RequestMetadata.FromRequest(request), options, ct);

    /// <inheritdoc />
    public ValueTask EnqueueWithOptionsAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
        RequestMetadata metadata, RequestQueueOptions options, CancellationToken ct = default)
        where TRequest : IRequest, IQueuable
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ct.ThrowIfCancellationRequested();
        return EnqueueCoreAsync(request, routeValues, actorToken, metadata, options, ct);
    }

    async ValueTask EnqueueCoreAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
        RequestMetadata metadata, RequestQueueOptions? options, CancellationToken ct)
        where TRequest : IRequest, IQueuable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(routeValues);

        var requestName = _catalog.Get(request.GetType()).Discriminator.Name;
        using var activity = PortiaTelemetry.StartSend(requestName, "queue", "fitz");
        var started = PortiaTelemetry.StartTimestamp();
        var outcome = "success";
        try
        {
            var route = FitzRouting.ResolveQueueRoute(_catalog, request, routeValues);
            var body = _serializer.Serialize(request, actorToken, metadata, PortiaTelemetry.CaptureTraceContext());
            if (options is null)
                _ = await _queue.EnqueueAsync(route, body, (TimeSpan?)null, ct).ConfigureAwait(false);
            else
                _ = await _queue.EnqueueAsync(route, body, new QueueEnqueueOptions(options.Delay,
                    options.TtlHours is { } hours ? TimeSpan.FromHours(hours) : null), ct).ConfigureAwait(false);
            PortiaTelemetry.RecordOutcome(activity, true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = "canceled";
            PortiaTelemetry.RecordCanceled(activity);
            throw;
        }
        catch (Exception ex)
        {
            outcome = "fault";
            PortiaTelemetry.RecordFault(activity, ex);
            throw;
        }
        finally
        {
            PortiaTelemetry.TransportFinished(started, "queue", "enqueue", outcome);
        }
    }
}
