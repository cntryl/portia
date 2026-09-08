using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Starts child work using explicit causal context and separately supplied transport credentials.</summary>
public static class RequestSenderContextExtensions
{
    /// <summary>Enqueues a new child request.</summary>
    public static ValueTask EnqueueAsync<TRequest>(this IRequestQueuePublisher sender, TRequest request,
        RequestRouteValues routeValues, string? actorToken, IExecutionContext parent, CancellationToken ct = default)
        where TRequest : IRequest, IQueuable
        => sender.EnqueueAsync(request, routeValues, actorToken, RequestMetadata.FromParent(parent), ct);

    /// <summary>Calls a new child request remotely.</summary>
    public static ValueTask<Result> SendAsync<TRequest>(this IRemoteRequestSender sender, TRequest request,
        RequestRouteValues routeValues, string? actorToken, IExecutionContext parent, CancellationToken ct = default)
        where TRequest : IRequest, ICallable
        => sender.SendAsync(request, routeValues, actorToken, RequestMetadata.FromParent(parent), ct);

    /// <summary>Calls a result-bearing child request remotely.</summary>
    public static ValueTask<Result<TOut>> SendAsync<TRequest, TOut>(this IRemoteRequestSender sender, TRequest request,
        RequestRouteValues routeValues, string? actorToken, IExecutionContext parent, CancellationToken ct = default)
        where TRequest : IRequest<TOut>, ICallable
        => sender.SendAsync<TRequest, TOut>(request, routeValues, actorToken, RequestMetadata.FromParent(parent), ct);

    /// <summary>Publishes a new child notification.</summary>
    public static ValueTask PublishAsync<TRequest>(this INoticeRequestSender sender, TRequest request,
        RequestRouteValues routeValues, string? actorToken, IExecutionContext parent, CancellationToken ct = default)
        where TRequest : IRequest, INotifiable
        => sender.PublishAsync(request, routeValues, actorToken, RequestMetadata.FromParent(parent), ct);

    /// <summary>Creates a schedule template caused by the current execution.</summary>
    public static ValueTask<string> ScheduleAsync<TRequest>(this IRequestScheduler sender, TRequest request,
        RequestScheduleSpec spec, RequestRouteValues routeValues, ClaimsPrincipal actor, IExecutionContext parent, CancellationToken ct = default)
        where TRequest : IRequest, ISchedulable
        => sender.ScheduleAsync(request, spec, routeValues, actor, RequestMetadata.FromParent(parent), ct);
}
