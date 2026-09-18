using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Starts child work using explicit causal context and separately supplied transport credentials.</summary>
public static class RequestSenderContextExtensions
{
    /// <summary>Enqueues a new child request.</summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="sender">The publisher that accepts the request.</param>
    /// <param name="request">The request to enqueue.</param>
    /// <param name="routeValues">
    ///     Values for any route segment the request's
    ///     <see cref="RequestRouteAttribute" /> left as <see cref="RequestRouteAttribute.Wildcard" />.
    /// </param>
    /// <param name="actorToken">
    ///     The enqueuing actor's raw bearer token, or <see langword="null" /> for
    ///     an unauthenticated actor.
    /// </param>
    /// <param name="parent">The execution this request inherits correlation and causation from.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the enqueue.</returns>
    public static ValueTask EnqueueAsync<TRequest>(this IRequestQueuePublisher sender, TRequest request,
        RequestRouteValues routeValues, string? actorToken, IExecutionContext parent, CancellationToken ct = default)
        where TRequest : IRequest, IQueuable
        => sender.EnqueueAsync(request, routeValues, actorToken, RequestMetadata.FromParent(parent), ct);

    /// <summary>Calls a new child request remotely.</summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="sender">The sender that carries the request.</param>
    /// <param name="request">The request to send.</param>
    /// <param name="routeValues">
    ///     Values for any route segment the request's
    ///     <see cref="RequestRouteAttribute" /> left as <see cref="RequestRouteAttribute.Wildcard" />.
    /// </param>
    /// <param name="actorToken">
    ///     The caller's raw bearer token, or <see langword="null" /> for an
    ///     unauthenticated actor.
    /// </param>
    /// <param name="parent">The execution this request inherits correlation and causation from.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of handling the request.</returns>
    public static ValueTask<Result> SendAsync<TRequest>(this IRemoteRequestSender sender, TRequest request,
        RequestRouteValues routeValues, string? actorToken, IExecutionContext parent, CancellationToken ct = default)
        where TRequest : IRequest, ICallable
        => sender.SendAsync(request, routeValues, actorToken, RequestMetadata.FromParent(parent), ct);

    /// <summary>Calls a result-bearing child request remotely.</summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    /// <param name="sender">The sender that carries the request.</param>
    /// <param name="request">The request to send.</param>
    /// <param name="routeValues">
    ///     Values for any route segment the request's
    ///     <see cref="RequestRouteAttribute" /> left as <see cref="RequestRouteAttribute.Wildcard" />.
    /// </param>
    /// <param name="actorToken">
    ///     The caller's raw bearer token, or <see langword="null" /> for an
    ///     unauthenticated actor.
    /// </param>
    /// <param name="parent">The execution this request inherits correlation and causation from.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The outcome of handling the request.</returns>
    public static ValueTask<Result<TOut>> SendAsync<TRequest, TOut>(this IRemoteRequestSender sender, TRequest request,
        RequestRouteValues routeValues, string? actorToken, IExecutionContext parent, CancellationToken ct = default)
        where TRequest : IRequest<TOut>, ICallable
        => sender.SendAsync<TRequest, TOut>(request, routeValues, actorToken, RequestMetadata.FromParent(parent), ct);

    /// <summary>Publishes a new child notification.</summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="sender">The sender that broadcasts the request.</param>
    /// <param name="request">The request to publish.</param>
    /// <param name="routeValues">
    ///     Values for any route segment the request's
    ///     <see cref="RequestRouteAttribute" /> left as <see cref="RequestRouteAttribute.Wildcard" />.
    /// </param>
    /// <param name="actorToken">
    ///     The publishing actor's raw bearer token, or <see langword="null" /> for
    ///     an unauthenticated actor.
    /// </param>
    /// <param name="parent">The execution this request inherits correlation and causation from.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the publish.</returns>
    public static ValueTask PublishAsync<TRequest>(this INoticeRequestSender sender, TRequest request,
        RequestRouteValues routeValues, string? actorToken, IExecutionContext parent, CancellationToken ct = default)
        where TRequest : IRequest, INotifiable
        => sender.PublishAsync(request, routeValues, actorToken, RequestMetadata.FromParent(parent), ct);

    /// <summary>Creates a schedule template caused by the current execution.</summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="sender">The scheduler that stores the template.</param>
    /// <param name="request">The request to schedule.</param>
    /// <param name="spec">When and how the request fires.</param>
    /// <param name="routeValues">
    ///     Values for any route segment the request's
    ///     <see cref="RequestRouteAttribute" /> left as <see cref="RequestRouteAttribute.Wildcard" />.
    /// </param>
    /// <param name="actor">
    ///     The explicit system identity under which every firing executes. User and
    ///     anonymous identities are rejected and no bearer credential is persisted.
    /// </param>
    /// <param name="parent">The execution this request inherits correlation and causation from.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>An identity that can later cancel the schedule.</returns>
    public static ValueTask<string> ScheduleAsync<TRequest>(this IRequestScheduler sender, TRequest request,
        RequestScheduleSpec spec, RequestRouteValues routeValues, ClaimsPrincipal actor, IExecutionContext parent,
        CancellationToken ct = default)
        where TRequest : IRequest, ISchedulable
        => sender.ScheduleAsync(request, spec, routeValues, actor, RequestMetadata.FromParent(parent), ct);
}
