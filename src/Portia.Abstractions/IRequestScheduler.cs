using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
///     Schedules a no-result request for future or recurring dispatch, independent of which
///     scheduling technology (Fitz, or anything else) carries it. A scheduled request's handler is
///     the same handler that runs when it originates any other way. Schedules are durable system
///     work: they never capture or persist a user bearer credential.
/// </summary>
public interface IRequestScheduler
{
    /// <summary>
    ///     Schedules a request. Only a request marked <see cref="ISchedulable" /> can be scheduled.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="request">The request to schedule.</param>
    /// <param name="spec">When and how the request fires.</param>
    /// <param name="routeValues">
    ///     Values for any route segment the request's
    ///     <see cref="RequestRouteAttribute" /> left as <see cref="RequestRouteAttribute.Wildcard" />.
    /// </param>
    /// <param name="actor">
    ///     The explicit system identity under which every firing executes. User
    ///     and anonymous identities are rejected and no bearer credential is persisted.
    /// </param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>An identity that can later cancel the schedule.</returns>
    ValueTask<string> ScheduleAsync<TRequest>(
        TRequest request,
        RequestScheduleSpec spec,
        RequestRouteValues routeValues,
        ClaimsPrincipal actor,
        CancellationToken ct = default)
        where TRequest : IRequest, ISchedulable
        => ScheduleAsync(request, spec, routeValues, actor, RequestMetadata.Create(), ct);

    /// <summary>Schedules a template with explicit causal identity.</summary>
    ValueTask<string> ScheduleAsync<TRequest>(
        TRequest request,
        RequestScheduleSpec spec,
        RequestRouteValues routeValues,
        ClaimsPrincipal actor,
        RequestMetadata metadata,
        CancellationToken ct = default)
        where TRequest : IRequest, ISchedulable;

    /// <summary>
    ///     Cancels a previously scheduled request.
    /// </summary>
    /// <param name="scheduleId">The identity returned by <c>ScheduleAsync</c>.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the cancellation.</returns>
    ValueTask CancelAsync(string scheduleId, CancellationToken ct = default);
}
