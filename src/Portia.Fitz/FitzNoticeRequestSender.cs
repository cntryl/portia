
namespace Cntryl.Portia;

/// <summary>
///     Publishes requests over Fitz live (ephemeral) notice fanout.
/// </summary>
/// <param name="notice">The Fitz notice client.</param>
/// <param name="serializer">The request serializer.</param>
/// <param name="catalog">Provides generated request routes.</param>
public sealed class FitzNoticeRequestSender(
    INoticeClient notice,
    IRequestSerializer serializer,
    RequestTransportCatalog? catalog = null) : INoticeRequestSender
{
    readonly RequestTransportCatalog _catalog = catalog ?? (serializer as JsonRequestSerializer)?.Catalog
        ?? throw new ArgumentNullException(nameof(catalog));

    readonly INoticeClient _notice = notice ?? throw new ArgumentNullException(nameof(notice));
    readonly IRequestSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

    /// <inheritdoc />
    public ValueTask PublishAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
        CancellationToken ct = default)
        where TRequest : IRequest, INotifiable
        => PublishAsync(request, routeValues, actorToken, RequestMetadata.Create(), ct);

    /// <inheritdoc />
    public async ValueTask PublishAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
        RequestMetadata metadata, CancellationToken ct = default)
        where TRequest : IRequest, INotifiable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(routeValues);

        var requestName = _catalog.Get(request.GetType()).Discriminator.Name;
        using var activity = PortiaTelemetry.StartSend(requestName, "notice", "fitz");
        var started = PortiaTelemetry.StartTimestamp();
        var outcome = "success";
        try
        {
            var route = FitzRouting.ResolveNoticeRoute(_catalog, request, routeValues);
            var body = _serializer.Serialize(request, actorToken, metadata, PortiaTelemetry.CaptureTraceContext());
            await _notice.PublishAsync(route, body, ct).ConfigureAwait(false);
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
            PortiaTelemetry.TransportFinished(started, "notice", "publish", outcome);
        }
    }
}
