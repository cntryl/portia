namespace Cntryl.Portia;

/// <summary>
/// Schedules a no-result request for future or recurring dispatch, independent of which
/// scheduling technology (Fitz, or anything else) carries it. A scheduled request's handler is
/// the same handler that runs when it originates any other way.
/// </summary>
public interface IRequestScheduler
{
    /// <summary>
    /// Schedules a request. Only a request marked <see cref="ISchedulable" /> can be scheduled.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="request">The request to schedule.</param>
    /// <param name="spec">When and how the request fires.</param>
    /// <param name="routeValues">Values for any route segment the request's
    /// <see cref="RequestRouteAttribute" /> left as <see cref="RequestRouteAttribute.Wildcard" />.</param>
    /// <param name="actorToken">The scheduling actor's raw bearer token, or <see langword="null" />
    /// for an unauthenticated actor. Re-validated — expiry included — at the moment the schedule
    /// actually fires, which may be long, or repeatedly, after it was scheduled. A recurring
    /// schedule whose token has since expired will start failing its authorization on every fire.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>An identity that can later cancel the schedule.</returns>
    ValueTask<string> ScheduleAsync<TRequest>(
        TRequest request,
        RequestScheduleSpec spec,
        RequestRouteValues routeValues,
        string? actorToken,
        CancellationToken ct = default)
        where TRequest : IRequest, ISchedulable;

    /// <summary>
    /// Cancels a previously scheduled request.
    /// </summary>
    /// <param name="scheduleId">The identity returned by <see cref="ScheduleAsync{TRequest}" />.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the cancellation.</returns>
    ValueTask CancelAsync(string scheduleId, CancellationToken ct = default);
}
