namespace Cntryl.Portia;

/// <summary>
/// Verifies that a request's transport marker interfaces (<see cref="ICallable" />,
/// <see cref="IQueuable" />, <see cref="INotifiable" />, <see cref="ISchedulable" />) are
/// compile-time enforced at each transport's call site, not just conventionally implied — and
/// that a result-bearing request can only ever be <see cref="ICallable" />, since queue/notice/
/// schedule have no return channel.
/// </summary>
public sealed class RequestTransportMarkerTests
{
    /// <summary>
    /// Verifies that a result-bearing request is accepted by the RPC sender.
    /// </summary>
    [Fact]
    public async Task ShouldAcceptCallableResultBearingRequestOverRpc()
    {
        // The real assertion is that this compiles: CreateUser only implements IRequest{Uuid}
        // and ICallable, not IRequest — so it could never be passed to EnqueueAsync/
        // PublishAsync/ScheduleAsync, all of which require plain IRequest.
        IRemoteRequestSender sender = new FakeRemoteRequestSender();
        var request = new CreateUser("ada@example.com", "Ada Lovelace");
        var routeValues = new RequestRouteValues(Realm: "tenant-123");

        var result = await sender.SendAsync<CreateUser, Uuid>(request, routeValues, actorToken: null);

        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// Verifies that a no-result request marked for every transport is accepted by every
    /// transport's sender/publisher/scheduler signature.
    /// </summary>
    [Fact]
    public async Task ShouldAcceptFullyMarkedNoResultRequestAcrossEveryTransportSignature()
    {
        // The real assertion is that this compiles: SendWelcomeEmail implements IRequest plus
        // all four marker interfaces, so every transport signature below accepts it.
        IRemoteRequestSender remoteSender = new FakeRemoteRequestSender();
        IRequestQueuePublisher queuePublisher = new FakeRequestQueuePublisher();
        INoticeRequestSender noticeSender = new FakeNoticeRequestSender();
        IRequestScheduler scheduler = new FakeRequestScheduler();
        var request = new SendWelcomeEmail(Uuid.CreateVersion4());
        var routeValues = new RequestRouteValues(Realm: "tenant-123");

        var sendResult = await remoteSender.SendAsync(request, routeValues, actorToken: null);
        await queuePublisher.EnqueueAsync(request, routeValues, actorToken: null);
        await noticeSender.PublishAsync(request, routeValues, actorToken: null);
        var scheduleId = await scheduler.ScheduleAsync(request, new RequestScheduleSpec("0 0 * * *"), routeValues, actorToken: null);

        Assert.True(sendResult.IsSuccess);
        Assert.NotNull(scheduleId);
    }

    sealed class FakeRemoteRequestSender : IRemoteRequestSender
    {
        public ValueTask<Result> SendAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
            where TRequest : IRequest, ICallable => ValueTask.FromResult(Result.Success);

        public ValueTask<Result<TOut>> SendAsync<TRequest, TOut>(TRequest request, RequestRouteValues routeValues, string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
            where TRequest : IRequest<TOut>, ICallable => ValueTask.FromResult(Result<TOut>.Success(default!));
    }

    sealed class FakeRequestQueuePublisher : IRequestQueuePublisher
    {
        public ValueTask EnqueueAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
            where TRequest : IRequest, IQueuable => ValueTask.CompletedTask;
    }

    sealed class FakeNoticeRequestSender : INoticeRequestSender
    {
        public ValueTask PublishAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
            where TRequest : IRequest, INotifiable => ValueTask.CompletedTask;
    }

    sealed class FakeRequestScheduler : IRequestScheduler
    {
        public ValueTask<string> ScheduleAsync<TRequest>(
            TRequest request,
            RequestScheduleSpec spec,
            RequestRouteValues routeValues,
            string? actorToken,
            RequestMetadata metadata,
            CancellationToken ct = default)
            where TRequest : IRequest, ISchedulable => ValueTask.FromResult("schedule-id");

        public ValueTask CancelAsync(string scheduleId, CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Creates a new user account. Result-bearing, so it can only ever be reached in-proc or over
/// RPC — <see cref="ICallable" /> is the only transport marker a request with a result can
/// implement.
/// </summary>
/// <param name="Email">The user's email address.</param>
/// <param name="DisplayName">The user's display name.</param>
[RequestRoute(realm: "*", area: "identity", resource: "users", operation: "create")]
[Discriminator("test.identity.create-user")]
public sealed record CreateUser(string Email, string DisplayName) : IRequest<Uuid>, ICallable;

/// <summary>
/// Sends a welcome email to a newly created user. No result, so it can be reached in-proc, over
/// RPC, over a queue, over live notification, or via schedule.
/// </summary>
/// <param name="UserId">The identity of the user to welcome.</param>
[RequestRoute(realm: "*", area: "identity", resource: "users", operation: "welcome")]
[Discriminator("test.identity.send-welcome")]
public sealed record SendWelcomeEmail(Uuid UserId) : IRequest, ICallable, IQueuable, INotifiable, ISchedulable;
