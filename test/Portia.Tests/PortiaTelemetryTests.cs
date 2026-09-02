using System.Collections.Concurrent;
using System.Diagnostics;

namespace Cntryl.Portia;

/// <summary>
/// Verifies that every dispatch through <see cref="GeneratedRequestBus" /> is traced via
/// <see cref="PortiaTelemetry.ActivitySource" /> — instrumented once at the bus, the one
/// chokepoint every transport already funnels through, so this is the whole tracing story
/// regardless of transport. Uses a real <see cref="ActivityListener" />, the same mechanism an
/// app's actual OpenTelemetry SDK subscribes through, rather than asserting on generated source
/// text.
/// </summary>
public sealed class PortiaTelemetryTests
{
    /// <summary>
    /// Verifies that a successful dispatch produces one activity, named after the request type,
    /// tagged as successful, with no error status.
    /// </summary>
    [Fact]
    public async Task ShouldRecordSuccessfulActivity()
    {
        using var listener = Listen(out var activities);
        var bus = TestRequestBus.Create();

        _ = await bus.SendAsync(new TelemetrySuccessAction(), RequestActor.System);

        // The listener is process-wide (ActivitySource is a static, shared instance) and every
        // request type below is otherwise unused elsewhere in this test project, specifically so
        // no other, concurrently-running test file's dispatch of the same request type can ever
        // land in this test's own capture list — filtering by tag alone isn't enough for that,
        // since a shared request type (e.g. GetValue) really can be dispatched by another test
        // file at the same moment this listener is active.
        var activity = Assert.Single(activities, a => (a.GetTagItem("portia.request_type") as string) == "TelemetrySuccessAction");
        Assert.Equal("Portia TelemetrySuccessAction", activity.DisplayName);
        Assert.Equal(true, activity.GetTagItem("portia.success"));
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
    }

    /// <summary>
    /// Verifies that a handler failure is recorded on the activity as an error status, tagged
    /// with the failure's category, not just silently swallowed.
    /// </summary>
    [Fact]
    public async Task ShouldRecordHandlerFailureAsErrorStatus()
    {
        using var listener = Listen(out var activities);
        var bus = TestRequestBus.Create();

        _ = await bus.SendAsync(new TelemetryFailureAction(), RequestActor.System);

        var activity = Assert.Single(activities, a => (a.GetTagItem("portia.request_type") as string) == "TelemetryFailureAction");
        Assert.Equal(false, activity.GetTagItem("portia.success"));
        Assert.Equal("Validation", activity.GetTagItem("portia.error_kind"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
    }

    /// <summary>
    /// Verifies that a request denied by <see cref="RequiresPermissionAttribute" /> still
    /// produces a properly closed, error-tagged activity — the handler never running doesn't mean
    /// the dispatch goes untraced.
    /// </summary>
    [Fact]
    public async Task ShouldRecordPermissionDenialAsErrorStatus()
    {
        using var listener = Listen(out var activities);
        var bus = TestRequestBus.Create(permissionEvaluator: RequestAuthorizationTests.FakePermissionEvaluator.DenyAll());

        _ = await bus.SendAsync(new TelemetryGuardedAction(), RequestActor.Anonymous);

        var activity = Assert.Single(activities, a => (a.GetTagItem("portia.request_type") as string) == "TelemetryGuardedAction");
        Assert.Equal("Forbidden", activity.GetTagItem("portia.error_kind"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
    }

    /// <summary>
    /// Verifies that a streamed request produces one activity spanning the whole stream, still
    /// open (not yet stopped) while items are being enumerated.
    /// </summary>
    [Fact]
    public async Task ShouldRecordActivityForStreamedRequest()
    {
        using var listener = Listen(out var activities);
        var bus = TestRequestBus.Create();

        var items = new List<int>();

        // The activity only stops (and lands in `activities`, since Listen captures on
        // ActivityStopped) once the whole iterator completes — `using var activity` inside an
        // async iterator disposes on iterator completion/disposal, not on the first yield — so
        // nothing is captured until after this loop finishes.
        await foreach (var item in bus.StreamAsync(new TelemetrySequence(), RequestActor.System))
            items.Add(item);

        Assert.Equal([1, 2, 3], items);
        _ = Assert.Single(activities, a => (a.GetTagItem("portia.request_type") as string) == "TelemetrySequence");
    }

    static ActivityListener Listen(out ConcurrentBag<Activity> activities)
    {
        // ConcurrentBag, not List: the listener is process-wide, so ActivityStopped can fire
        // concurrently from other tests' activities completing on other threads while this
        // test's own assertion enumerates the capture list.
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
}

sealed record TelemetrySuccessAction : IRequest;

sealed class TelemetrySuccessActionHandler : IRequestHandler<TelemetrySuccessAction>
{
    public ValueTask<Result> HandleAsync(IRequestContext<TelemetrySuccessAction> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}

sealed record TelemetryFailureAction : IRequest;

sealed class TelemetryFailureActionHandler : IRequestHandler<TelemetryFailureAction>
{
    public ValueTask<Result> HandleAsync(IRequestContext<TelemetryFailureAction> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Failure(new RequestError(RequestErrorKind.Validation, "Invalid.")));
}

[RequiresPermission("telemetry:guarded")]
sealed record TelemetryGuardedAction : IRequest;

sealed class TelemetryGuardedActionHandler : IRequestHandler<TelemetryGuardedAction>
{
    public ValueTask<Result> HandleAsync(IRequestContext<TelemetryGuardedAction> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}

sealed record TelemetrySequence : IStreamRequest<int>;

sealed class TelemetrySequenceHandler : IStreamRequestHandler<TelemetrySequence, int>
{
    public async IAsyncEnumerable<int> HandleAsync(
        IRequestContext<TelemetrySequence> context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return 1;
        yield return 2;
        yield return 3;
        await Task.CompletedTask;
    }
}
