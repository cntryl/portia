using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
///     Verifies that a request's transport marker interfaces (<see cref="ICallable" />,
///     <see cref="IQueuable" />, <see cref="INotifiable" />, <see cref="ISchedulable" />) are
///     compile-time enforced at each transport's call site, not just conventionally implied — and
///     that a result-bearing request can only ever be <see cref="ICallable" />, since queue/notice/
///     schedule have no return channel.
/// </summary>
public sealed class RequestTransportMarkerTests
{
    /// <summary>A child send cannot silently become a new root operation when its parent is missing.</summary>
    [Fact]
    public async Task ShouldRejectMissingParentGivenChildTransportSend()
    {
        // Arrange
        var implementation = new FakeRemoteRequestSender();
        IRemoteRequestSender sender = implementation;
        var request = new RequestIdentityTests.IdentifiedCommand(Uuid.CreateVersion4());

        // Act and Assert
        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await sender.SendAsync(request, new RequestRouteValues(), null, (IExecutionContext)null!));
        Assert.Null(implementation.LastMetadata);
    }

    /// <summary>Default transport helpers carry the caller's typed operation identity.</summary>
    [Fact]
    public async Task ShouldPreserveTypedIdentityGivenDefaultTransportHelpers()
    {
        // Arrange
        var remote = new FakeRemoteRequestSender();
        var queue = new FakeRequestQueuePublisher();
        var notice = new FakeNoticeRequestSender();
        var scheduler = new FakeRequestScheduler();
        var request = new RequestIdentityTests.IdentifiedCommand(Uuid.CreateVersion4());
        var route = new RequestRouteValues();

        // Act and Assert
        _ = await ((IRemoteRequestSender)remote).SendAsync(request, route, null);
        Assert.Equal(request.CommandId, remote.LastMetadata!.RequestId);
        _ = await ((IRemoteRequestSender)remote).SendWithOutcomeAsync(request, route, null);
        Assert.Equal(request.CommandId, remote.LastMetadata!.RequestId);
        var query = new RequestIdentityTests.IdentifiedQuery(Uuid.CreateVersion4());
        _ = await ((IRemoteRequestSender)remote).SendAsync<RequestIdentityTests.IdentifiedQuery, Uuid>(query, route, null);
        Assert.Equal(query.CommandId, remote.LastMetadata!.RequestId);
        _ = await ((IRemoteRequestSender)remote).SendWithOutcomeAsync<RequestIdentityTests.IdentifiedQuery, Uuid>(query, route, null);
        Assert.Equal(query.CommandId, remote.LastMetadata!.RequestId);
        await ((IRequestQueuePublisher)queue).EnqueueAsync(request, route, null);
        Assert.Equal(request.CommandId, queue.LastMetadata!.RequestId);
        await ((INoticeRequestSender)notice).PublishAsync(request, route, null);
        _ = await ((IRequestScheduler)scheduler).ScheduleAsync(request, new RequestScheduleSpec("* * * * *"),
            route, RequestActor.System);
        Assert.Equal(request.CommandId, notice.LastMetadata!.RequestId);
        Assert.Equal(request.CommandId, scheduler.LastMetadata!.RequestId);
    }

    /// <summary>
    ///     Verifies that a result-bearing request is accepted by the RPC sender.
    /// </summary>
    [Fact]
    public async Task ShouldAcceptCallableResultBearingRequestOverRpc()
    {
        // The real assertion is that this compiles: CreateUser only implements IRequest{Uuid}
        // and ICallable, not IRequest — so it could never be passed to EnqueueAsync/
        // PublishAsync/ScheduleAsync, all of which require plain IRequest.
        IRemoteRequestSender sender = new FakeRemoteRequestSender();
        var request = new CreateUser("ada@example.com", "Ada Lovelace");
        var routeValues = new RequestRouteValues("tenant-123");

        var result = await sender.SendAsync<CreateUser, Uuid>(request, routeValues, null);

        Assert.True(result.IsSuccess);
    }

    /// <summary>
    ///     Verifies that a no-result request marked for every transport is accepted by every
    ///     transport's sender/publisher/scheduler signature.
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
        var routeValues = new RequestRouteValues("tenant-123");

        var sendResult = await remoteSender.SendAsync(request, routeValues, null);
        await queuePublisher.EnqueueAsync(request, routeValues, null);
        await noticeSender.PublishAsync(request, routeValues, null);
        var scheduleId = await scheduler.ScheduleAsync(request, new RequestScheduleSpec("0 0 * * *"), routeValues,
            RequestActor.System);

        Assert.True(sendResult.IsSuccess);
        Assert.NotNull(scheduleId);
    }

    /// <summary>A legacy sender implementation reports completed results through the default outcome API.</summary>
    [Fact]
    public async Task ShouldReportCompletedOutcomeForLegacySenderImplementation()
    {
        IRemoteRequestSender sender = new FakeRemoteRequestSender();

        var outcome = await sender.SendWithOutcomeAsync<CreateUser, Uuid>(
            new CreateUser("ada@example.com", "Ada Lovelace"), new RequestRouteValues("tenant-123"), null);

        var completed = Assert.IsType<RemoteRequestOutcome<Uuid>.Completed>(outcome);
        Assert.True(completed.Result.IsSuccess);
    }

    /// <summary>A legacy sender returns not-dispatched without invoking the implementation for prior cancellation.</summary>
    [Fact]
    public async Task ShouldReportNotDispatchedOutcomeForPreCancelledLegacySender()
    {
        var implementation = new FakeRemoteRequestSender();
        IRemoteRequestSender sender = implementation;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var outcome = await sender.SendWithOutcomeAsync<CreateUser, Uuid>(
            new CreateUser("ada@example.com", "Ada Lovelace"), new RequestRouteValues("tenant-123"), null,
            cancellation.Token);

        _ = Assert.IsType<RemoteRequestOutcome<Uuid>.NotDispatched>(outcome);
        Assert.Equal(0, implementation.CallCount);
    }

    /// <summary>A legacy sender reports an unknown outcome when cancellation interrupts an active send.</summary>
    [Fact]
    public async Task ShouldReportUnknownOutcomeWhenLegacySenderIsCancelledAfterDispatch()
    {
        var implementation = new WaitingRemoteRequestSender();
        IRemoteRequestSender sender = implementation;
        using var cancellation = new CancellationTokenSource();
        var sending = sender.SendWithOutcomeAsync<CreateUser, Uuid>(
            new CreateUser("ada@example.com", "Ada Lovelace"), new RequestRouteValues("tenant-123"), null,
            cancellation.Token).AsTask();

        await implementation.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var outcome = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        _ = Assert.IsType<RemoteRequestOutcome<Uuid>.OutcomeUnknown>(outcome);
    }

    /// <summary>Outcome-aware child sends inherit their parent's correlation and causation identities.</summary>
    [Fact]
    public async Task ShouldPreserveParentContextWhenSendingOutcomeAwareRequests()
    {
        var parentMetadata = new RequestMetadata(Uuid.CreateVersion4(), Uuid.CreateVersion4(), Uuid.CreateVersion4());
        var parent = new RequestContext<ClockProbe>(new ClockProbe(),
            new RequestDispatchContext(RequestActor.System, metadata: parentMetadata));
        var implementation = new FakeRemoteRequestSender();
        IRemoteRequestSender sender = implementation;
        var routeValues = new RequestRouteValues("tenant-123");

        var noResultOutcome = await sender.SendWithOutcomeAsync(
            new SendWelcomeEmail(Uuid.CreateVersion4()), routeValues, null, parent);
        var noResultMetadata = Assert.IsType<RequestMetadata>(implementation.LastMetadata);
        _ = Assert.IsType<RemoteRequestOutcome.Completed>(noResultOutcome);
        Assert.NotEqual(parent.RequestId, noResultMetadata.RequestId);
        Assert.Equal(parent.CorrelationId, noResultMetadata.CorrelationId);
        Assert.Equal(parent.RequestId, noResultMetadata.CausationId);

        var resultOutcome = await sender.SendWithOutcomeAsync<CreateUser, Uuid>(
            new CreateUser("ada@example.com", "Ada Lovelace"), routeValues, null, parent);
        var resultMetadata = Assert.IsType<RequestMetadata>(implementation.LastMetadata);
        _ = Assert.IsType<RemoteRequestOutcome<Uuid>.Completed>(resultOutcome);
        Assert.NotEqual(parent.RequestId, resultMetadata.RequestId);
        Assert.Equal(parent.CorrelationId, resultMetadata.CorrelationId);
        Assert.Equal(parent.RequestId, resultMetadata.CausationId);
    }

    sealed class FakeRemoteRequestSender : IRemoteRequestSender
    {
        public int CallCount { get; private set; }

        public RequestMetadata? LastMetadata { get; private set; }

        public ValueTask<Result> SendAsync<TRequest>(TRequest request, RequestRouteValues routeValues,
            string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
            where TRequest : IRequest, ICallable
        {
            LastMetadata = metadata;
            return ValueTask.FromResult(Result.Success);
        }

        public ValueTask<Result<TOut>> SendAsync<TRequest, TOut>(TRequest request, RequestRouteValues routeValues,
            string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
            where TRequest : IRequest<TOut>, ICallable
        {
            CallCount++;
            LastMetadata = metadata;
            return ValueTask.FromResult(Result<TOut>.Success(default!));
        }
    }

    sealed class WaitingRemoteRequestSender : IRemoteRequestSender
    {
        public TaskCompletionSource Dispatched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<Result> SendAsync<TRequest>(TRequest request, RequestRouteValues routeValues,
            string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
            where TRequest : IRequest, ICallable => new(WaitAsync(ct));

        public ValueTask<Result<TOut>> SendAsync<TRequest, TOut>(TRequest request, RequestRouteValues routeValues,
            string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
            where TRequest : IRequest<TOut>, ICallable => new(WaitAsync<TOut>(ct));

        async Task<Result> WaitAsync(CancellationToken ct)
        {
            Dispatched.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Result.Success;
        }

        async Task<Result<TOut>> WaitAsync<TOut>(CancellationToken ct)
        {
            Dispatched.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Result<TOut>.Success(default!);
        }
    }

    sealed class FakeRequestQueuePublisher : IRequestQueuePublisher
    {
        public RequestMetadata? LastMetadata { get; private set; }

        public ValueTask EnqueueAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
            RequestMetadata metadata, CancellationToken ct = default)
            where TRequest : IRequest, IQueuable
        {
            LastMetadata = metadata;
            return ValueTask.CompletedTask;
        }
    }

    sealed class FakeNoticeRequestSender : INoticeRequestSender
    {
        public RequestMetadata? LastMetadata { get; private set; }

        public ValueTask PublishAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
            RequestMetadata metadata, CancellationToken ct = default)
            where TRequest : IRequest, INotifiable
        {
            LastMetadata = metadata;
            return ValueTask.CompletedTask;
        }
    }

    sealed class FakeRequestScheduler : IRequestScheduler
    {
        public RequestMetadata? LastMetadata { get; private set; }

        public ValueTask<string> ScheduleAsync<TRequest>(
            TRequest request,
            RequestScheduleSpec spec,
            RequestRouteValues routeValues,
            ClaimsPrincipal actor,
            RequestMetadata metadata,
            CancellationToken ct = default)
            where TRequest : IRequest, ISchedulable
        {
            LastMetadata = metadata;
            return ValueTask.FromResult("schedule-id");
        }

        public ValueTask CancelAsync(string scheduleId, CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}
