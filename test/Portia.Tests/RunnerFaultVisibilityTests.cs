using System.Collections.Concurrent;
using System.Diagnostics;

namespace Cntryl.Portia;

/// <summary>
/// Verifies that every background runner records a visible, traced fault — via
/// <see cref="PortiaTelemetry.RecordRunnerFault" /> — for the paths that today silently drop or
/// abandon work: an actor token that fails re-validation, an unrecognized exception during
/// dispatch, and (for <see cref="MultiTenantRunner" />) a per-tenant callback that faults, which
/// previously stayed invisible until that tenant was next stopped. No one should ever have to
/// add logging inside Portia itself to find out why a request stalled or vanished.
/// </summary>
public sealed class RunnerFaultVisibilityTests
{
    /// <summary>
    /// Verifies that a queued request whose actor token fails re-validation records a visible
    /// fault instead of just silently completing (dropping) it.
    /// </summary>
    [Fact]
    public async Task ShouldRecordFaultWhenQueuedRequestActorTokenFailsRevalidation()
    {
        using var listener = Listen(out var activities);
        var bus = TestRequestBus.Create();
        var consumer = new FakeQueueConsumer([
            new FakeQueuedRequest(new RunnerFaultAction(), actorToken: "expired"),
        ]);
        var runner = new QueueRunner(consumer, bus, new FakeRequestActorValidator(rejectToken: "expired"));

        await runner.RunAsync();

        static bool Matches(Activity a)
        {
            return (a.GetTagItem("portia.runner") as string) == nameof(QueueRunner) && (a.GetTagItem("portia.fault_reason") as string) == "actor validation failed";
        }
        Assert.Contains(activities, Matches);
        var activity = activities.First(Matches);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
    }

    /// <summary>
    /// Verifies that an unrecognized exception during a queued dispatch records a fault carrying
    /// the exception itself, not just an abandoned request with no explanation.
    /// </summary>
    [Fact]
    public async Task ShouldRecordFaultWithExceptionWhenQueuedRequestDispatchThrows()
    {
        using var listener = Listen(out var activities);
        var bus = TestRequestBus.Create();
        var consumer = new FakeQueueConsumer([new FakeQueuedRequest(new UnregisteredRunnerFaultAction())]);
        var runner = new QueueRunner(consumer, bus, new FakeRequestActorValidator());

        await runner.RunAsync();

        static bool Matches(Activity a)
        {
            return (a.GetTagItem("portia.runner") as string) == nameof(QueueRunner) && (a.GetTagItem("portia.fault_reason") as string) == "unrecognized exception";
        }
        Assert.Contains(activities, Matches);
        var activity = activities.First(Matches);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Contains(activity.Events, e => e.Name == "exception");
    }

    /// <summary>
    /// Verifies that a live-delivered request whose actor token fails re-validation records a
    /// visible fault instead of just silently skipping it.
    /// </summary>
    [Fact]
    public async Task ShouldRecordFaultWhenLiveRequestActorTokenFailsRevalidation()
    {
        using var listener = Listen(out var activities);
        var bus = TestRequestBus.Create();
        var consumer = new FakeLiveRequestConsumer([new LiveRequest(new RunnerFaultAction(), ActorToken: "expired")]);
        var runner = new LiveRequestRunner(consumer, bus, new FakeRequestActorValidator(rejectToken: "expired"));

        await runner.RunAsync();

        static bool Matches(Activity a)
        {
            return (a.GetTagItem("portia.runner") as string) == nameof(LiveRequestRunner) && (a.GetTagItem("portia.fault_reason") as string) == "actor validation failed";
        }
        Assert.Contains(activities, Matches);
        _ = activities.First(Matches);
    }

    /// <summary>
    /// Verifies that an unrecognized exception during a live dispatch records a fault carrying
    /// the exception itself, not just a silently lost delivery.
    /// </summary>
    [Fact]
    public async Task ShouldRecordFaultWithExceptionWhenLiveRequestDispatchThrows()
    {
        using var listener = Listen(out var activities);
        var bus = TestRequestBus.Create();
        var consumer = new FakeLiveRequestConsumer([new LiveRequest(new UnregisteredRunnerFaultAction(), ActorToken: null)]);
        var runner = new LiveRequestRunner(consumer, bus, new FakeRequestActorValidator());

        await runner.RunAsync();

        static bool Matches(Activity a)
        {
            return (a.GetTagItem("portia.runner") as string) == nameof(LiveRequestRunner) && (a.GetTagItem("portia.fault_reason") as string) == "unrecognized exception";
        }
        Assert.Contains(activities, Matches);
        var activity = activities.First(Matches);
        Assert.Contains(activity.Events, e => e.Name == "exception");
    }

    /// <summary>
    /// Verifies that a tenant's start callback faulting is recorded the moment it faults, not
    /// only discovered later when that tenant happens to be stopped.
    /// </summary>
    [Fact]
    public async Task ShouldRecordFaultAsSoonAsTenantStartCallbackFaults()
    {
        using var listener = Listen(out var activities);
        var directory = new FakeTenantDirectory([new TenantId("acme")]);
        var runner = new MultiTenantRunner(directory);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(
            onTenantStarted: (_, _) => throw new InvalidOperationException("boom"),
            onTenantStopped: (_, _) => Task.CompletedTask,
            cts.Token);

        // Give the faulting start callback's continuation a chance to run before this test ever
        // stops the tenant — that's the whole point: it must be visible before shutdown, not
        // only as a byproduct of it.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!activities.Any(a => (a.GetTagItem("portia.runner") as string) == nameof(MultiTenantRunner)) && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        cts.Cancel();
        await run;

        Assert.Contains(activities, a => (a.GetTagItem("portia.runner") as string) == nameof(MultiTenantRunner));
        var activity = activities.First(a => (a.GetTagItem("portia.runner") as string) == nameof(MultiTenantRunner));
        Assert.Contains("acme", (string?)activity.GetTagItem("portia.fault_reason"), StringComparison.Ordinal);
        Assert.Contains(activity.Events, e => e.Name == "exception");
    }

    static ActivityListener Listen(out ConcurrentBag<Activity> activities)
    {
        var captured = new ConcurrentBag<Activity>();
        activities = captured;

        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PortiaTelemetry.SourceName,
            Sample = static (ref options) => ActivitySamplingResult.AllData,
            ActivityStopped = captured.Add,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    sealed class FakeQueueConsumer(IReadOnlyList<IQueuedRequest> items) : IRequestQueueConsumer
    {
        public async IAsyncEnumerable<IQueuedRequest> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return item;
            }
        }
    }

    sealed class FakeQueuedRequest(IRequest request, string? actorToken = null) : IQueuedRequest
    {
        public IRequest Request { get; } = request;

        public string? ActorToken { get; } = actorToken;

        public uint Attempt => 1;

        public ValueTask CompleteAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask AbandonAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    sealed class FakeLiveRequestConsumer(IReadOnlyList<LiveRequest> items) : ILiveRequestConsumer
    {
        public async IAsyncEnumerable<LiveRequest> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return item;
            }
        }
    }

    sealed class FakeRequestActorValidator(string? rejectToken = null) : IRequestActorValidator
    {
        public ValueTask<Result<System.Security.Claims.ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default) =>
            ValueTask.FromResult(token is not null && token == rejectToken
                ? Result<System.Security.Claims.ClaimsPrincipal>.Failure(new RequestError(RequestErrorKind.Unauthorized, "Token expired."))
                : Result<System.Security.Claims.ClaimsPrincipal>.Success(RequestActor.System));
    }

    sealed class FakeTenantDirectory(IReadOnlyList<TenantId> initial) : ITenantDirectory
    {
        public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var tenantId in initial)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return tenantId;
            }
        }

        public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (ct.Register(() => tcs.TrySetResult()))
                await tcs.Task.ConfigureAwait(false);

            yield break;
        }
    }

    internal sealed record RunnerFaultAction : IRequest;

    internal sealed class RunnerFaultActionHandler : IRequestHandler<RunnerFaultAction>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<RunnerFaultAction> context, CancellationToken ct) =>
            ValueTask.FromResult(Result.Success);
    }

    // Deliberately has no registered handler anywhere in the compilation, so dispatching it
    // throws the framework's own "no handler" exception — the unrecognized-exception path.
    sealed record UnregisteredRunnerFaultAction : IRequest;
}
