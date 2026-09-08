using Cntryl.Fitz.Abstractions.Domains.Schedule;

namespace Cntryl.Portia;

/// <summary>
/// Schedules requests for future or recurring dispatch through Fitz's schedule domain.
/// </summary>
/// <param name="schedule">The Fitz schedule client.</param>
/// <param name="serializer">The request serializer.</param>
/// <param name="catalog">Provides generated request routes.</param>
public sealed class FitzRequestScheduler(IScheduleClient schedule, IRequestSerializer serializer, RequestTransportCatalog? catalog = null) : IRequestScheduler
{
    readonly IScheduleClient _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
    readonly IRequestSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    readonly RequestTransportCatalog _catalog = catalog ?? (serializer as JsonRequestSerializer)?.Catalog
        ?? throw new ArgumentNullException(nameof(catalog));

    /// <inheritdoc />
    public ValueTask<string> ScheduleAsync<TRequest>(
        TRequest request,
        RequestScheduleSpec spec,
        RequestRouteValues routeValues,
        string? actorToken,
        CancellationToken ct = default)
        where TRequest : IRequest, ISchedulable
        => ScheduleAsync(request, spec, routeValues, actorToken, RequestMetadata.Create(), ct);

    /// <inheritdoc />
    public async ValueTask<string> ScheduleAsync<TRequest>(
        TRequest request,
        RequestScheduleSpec spec,
        RequestRouteValues routeValues,
        string? actorToken,
        RequestMetadata metadata,
        CancellationToken ct = default)
        where TRequest : IRequest, ISchedulable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(routeValues);

        using var activity = PortiaTelemetry.StartSend(typeof(TRequest).Name, "fitz.schedule");
        var started = PortiaTelemetry.StartTimestamp();
        var outcome = "success";
        try
        {
            var route = FitzRouting.ResolveScheduleRoute(_catalog, request, routeValues);
            var body = _serializer.Serialize(request, actorToken, metadata, PortiaTelemetry.CaptureTraceContext());
            var scheduleId = await _schedule.CreateAsync(route, spec.Cron, ToFitzDeliveryMode(spec.DeliveryMode), body.ToArray(), ct).ConfigureAwait(false);
            return scheduleId ?? throw new InvalidOperationException($"Scheduling request over route '{route}' did not return an identity.");
        }
        catch (OperationCanceledException) { outcome = "canceled"; throw; }
        catch { outcome = "fault"; throw; }
        finally { PortiaTelemetry.TransportFinished(started, "fitz.schedule", "schedule", outcome); }
    }

    /// <inheritdoc />
    public async ValueTask CancelAsync(string scheduleId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        await _schedule.CancelAsync(scheduleId, ct).ConfigureAwait(false);
    }

    static ScheduleDeliveryMode ToFitzDeliveryMode(RequestScheduleDeliveryMode mode) => mode switch
    {
        RequestScheduleDeliveryMode.One => ScheduleDeliveryMode.Single,
        RequestScheduleDeliveryMode.Broadcast => ScheduleDeliveryMode.Broadcast,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
