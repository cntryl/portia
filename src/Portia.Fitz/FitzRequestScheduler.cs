using System.Security.Claims;
using System.Text.Json;

namespace Cntryl.Portia;

/// <summary>
///     Schedules requests for future or recurring dispatch through Fitz's schedule domain.
/// </summary>
/// <param name="schedule">The Fitz schedule client.</param>
/// <param name="serializer">The request serializer.</param>
/// <param name="catalog">Provides generated request routes.</param>
public sealed class FitzRequestScheduler(
    IScheduleClient schedule,
    IRequestSerializer serializer,
    RequestTransportCatalog? catalog = null) : IRequestScheduler
{
    readonly RequestTransportCatalog _catalog = catalog ?? (serializer as JsonRequestSerializer)?.Catalog
        ?? throw new ArgumentNullException(nameof(catalog));

    readonly IScheduleClient _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
    readonly IRequestSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

    /// <inheritdoc />
    public ValueTask<string> EnsureAsync<TRequest>(TRequest request, RequestScheduleSpec spec,
        RequestRouteValues routeValues, ClaimsPrincipal actor, CancellationToken ct = default)
        where TRequest : IRequest, ISchedulable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(routeValues);
        ArgumentNullException.ThrowIfNull(actor);
        var route = FitzRouting.ResolveScheduleRoute(_catalog, request, routeValues);
        var id = Uuid.CreateVersion5(Uuid.UrlNamespace, "portia:schedule:" + route);
        return CreateAsync(request, spec, actor, new RequestMetadata(id, id), route, false, ct);
    }

    /// <inheritdoc />
    public ValueTask<string> ScheduleAsync<TRequest>(
        TRequest request,
        RequestScheduleSpec spec,
        RequestRouteValues routeValues,
        ClaimsPrincipal actor,
        CancellationToken ct = default)
        where TRequest : IRequest, ISchedulable
        => ScheduleAsync(request, spec, routeValues, actor, RequestMetadata.Create(), ct);

    /// <inheritdoc />
    public async ValueTask<string> ScheduleAsync<TRequest>(
        TRequest request,
        RequestScheduleSpec spec,
        RequestRouteValues routeValues,
        ClaimsPrincipal actor,
        RequestMetadata metadata,
        CancellationToken ct = default)
        where TRequest : IRequest, ISchedulable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(routeValues);
        ArgumentNullException.ThrowIfNull(actor);
        var route = FitzRouting.ResolveScheduleRoute(_catalog, request, routeValues);
        return await CreateAsync(request, spec, actor, metadata, route, true, ct)
            .ConfigureAwait(false);
    }

    async ValueTask<string> CreateAsync<TRequest>(TRequest request, RequestScheduleSpec spec,
        ClaimsPrincipal actor, RequestMetadata metadata, string route, bool propagateTraceContext,
        CancellationToken ct) where TRequest : IRequest, ISchedulable
    {
        if (!RequestActor.IsSystem(actor))
        {
            throw new ArgumentException("Scheduled requests must execute as an explicit Portia system identity.",
                nameof(actor));
        }

        var subject = actor.FindFirst(ClaimTypes.NameIdentifier)
                      ?? throw new ArgumentException("A scheduled system identity requires a subject.", nameof(actor));

        var requestName = _catalog.Get(request.GetType()).Discriminator.Name;
        using var activity = PortiaTelemetry.StartSend(requestName, "schedule", "fitz");
        var started = PortiaTelemetry.StartTimestamp();
        var outcome = "success";
        try
        {
            var traceContext = propagateTraceContext ? PortiaTelemetry.CaptureTraceContext() : null;
            var requestEnvelope = _serializer.Serialize(request, null, metadata, traceContext);
            var body = JsonSerializer.SerializeToUtf8Bytes(
                new FitzScheduledRequestEnvelope(1, subject.Value, subject.Issuer, requestEnvelope.ToArray()),
                FitzJsonContext.Default.FitzScheduledRequestEnvelope);
            var scheduleId = await _schedule
                .CreateAsync(route, spec.Cron, ToFitzDeliveryMode(spec.DeliveryMode), body.ToArray(), ct)
                .ConfigureAwait(false);
            var identity = scheduleId ??
                           throw new InvalidOperationException("Scheduling the request did not return an identity.");
            PortiaTelemetry.RecordOutcome(activity, true, null);
            return identity;
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
            PortiaTelemetry.TransportFinished(started, "schedule", "schedule", outcome);
        }
    }

    /// <inheritdoc />
    public async ValueTask CancelAsync(string scheduleId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        await _schedule.CancelAsync(scheduleId, ct).ConfigureAwait(false);
    }

    static ScheduleDeliveryMode ToFitzDeliveryMode(RequestScheduleDeliveryMode mode) => mode switch
    {
        RequestScheduleDeliveryMode.One => ScheduleDeliveryMode.Once,
        RequestScheduleDeliveryMode.Broadcast => ScheduleDeliveryMode.Broadcast,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}
