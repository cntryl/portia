using Cntryl.Fitz.Abstractions.Domains.Schedule;

namespace Cntryl.Portia;

/// <summary>
/// Schedules requests for future or recurring dispatch through Fitz's schedule domain.
/// </summary>
/// <param name="schedule">The Fitz schedule client.</param>
/// <param name="serializer">The request serializer.</param>
public sealed class FitzRequestScheduler(IScheduleClient schedule, IRequestSerializer serializer) : IRequestScheduler
{
    readonly IScheduleClient _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
    readonly IRequestSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

    /// <inheritdoc />
    public async ValueTask<string> ScheduleAsync<TRequest>(
        TRequest request,
        RequestScheduleSpec spec,
        RequestRouteValues routeValues,
        string? actorToken,
        CancellationToken ct = default)
        where TRequest : IRequest, ISchedulable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(routeValues);

        var route = FitzRouting.ResolveScheduleRoute(request, routeValues);
        var body = _serializer.Serialize(request, actorToken);
        var scheduleId = await _schedule
            .CreateAsync(route, spec.Cron, ToFitzDeliveryMode(spec.DeliveryMode), body.ToArray(), ct)
            .ConfigureAwait(false);

        return scheduleId ?? throw new InvalidOperationException($"Scheduling request over route '{route}' did not return an identity.");
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
