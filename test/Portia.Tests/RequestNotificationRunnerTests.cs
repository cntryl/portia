using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

/// <summary>
///     Verifies that one-way request notifications (Fitz notice fanout, a fired Fitz schedule entry)
///     dispatch through the same request bus as any other origin, and that this transport's
///     no-redelivery guarantee — a failed dispatch is simply lost, never retried — holds for both an
///     ordinary handler failure and an actor token that fails re-validation.
/// </summary>
public sealed class RequestNotificationRunnerTests
{
    /// <summary>
    ///     Verifies that every delivered request is dispatched to its handler.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchEveryDeliveredRequest()
    {
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var bus = busHost.Bus;
        var consumer = new FakeRequestNotificationConsumer([
            new RequestNotification(new ChangeValue(1), "valid-token", RequestMetadata.Create(),
                new NoticeInvocation("notice://test/work/item")),
            new RequestNotification(new ChangeValue(2), "valid-token", RequestMetadata.Create(),
                new NoticeInvocation("notice://test/work/item")),
            new RequestNotification(new ChangeValue(3), "valid-token", RequestMetadata.Create(),
                new NoticeInvocation("notice://test/work/item"))
        ]);
        var runner =
            new RequestNotificationRunner(consumer, RequestDeliveryScopes.Fixed(bus, new TestRequestActorValidator()));

        await runner.RunAsync();

        Assert.Equal(3, handler.LastValue);
    }

    /// <summary>
    ///     Verifies that a dispatch failure doesn't stop the run — later deliveries still dispatch,
    ///     matching this transport's "simply lost, not retried" guarantee (there's nothing to
    ///     abandon or redeliver at this level).
    /// </summary>
    [Fact]
    public async Task ShouldContinueAfterDispatchFailure()
    {
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var bus = busHost.Bus;
        var consumer = new FakeRequestNotificationConsumer([
            new RequestNotification(new ThrowingChangeValue(0), "valid-token", RequestMetadata.Create(),
                new NoticeInvocation("notice://test/work/item")),
            new RequestNotification(new ChangeValue(2), "valid-token", RequestMetadata.Create(),
                new NoticeInvocation("notice://test/work/item"))
        ]);
        var runner =
            new RequestNotificationRunner(consumer, RequestDeliveryScopes.Fixed(bus, new TestRequestActorValidator()));

        await runner.RunAsync();

        Assert.Equal(2, handler.LastValue);
    }

    /// <summary>
    ///     Verifies that a request whose carried actor token fails re-validation — e.g. it expired
    ///     since it was scheduled or published — never reaches its handler. Expiry is honored the
    ///     same way here as it is on the queue path, even though there's no redelivery decision to
    ///     make for this transport shape.
    /// </summary>
    [Fact]
    public async Task ShouldSkipDispatchWhenActorTokenFailsRevalidation()
    {
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var bus = busHost.Bus;
        var consumer = new FakeRequestNotificationConsumer([
            new RequestNotification(new ChangeValue(1), "expired-token", RequestMetadata.Create(),
                new NoticeInvocation("notice://test/work/item"))
        ]);
        var runner = new RequestNotificationRunner(consumer,
            RequestDeliveryScopes.Fixed(bus, new TestRequestActorValidator("expired-token")));

        await runner.RunAsync();

        Assert.Null(handler.LastValue);
    }

    /// <summary>
    ///     Verifies that a request whose actor token fails re-validation doesn't stop the run — the
    ///     deliveries that follow it still dispatch normally.
    /// </summary>
    [Fact]
    public async Task ShouldContinueAfterActorTokenRevalidationFailure()
    {
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var bus = busHost.Bus;
        var consumer = new FakeRequestNotificationConsumer([
            new RequestNotification(new ChangeValue(1), "expired-token", RequestMetadata.Create(),
                new NoticeInvocation("notice://test/work/item")),
            new RequestNotification(new ChangeValue(2), "valid-token", RequestMetadata.Create(),
                new NoticeInvocation("notice://test/work/item"))
        ]);
        var runner = new RequestNotificationRunner(consumer,
            RequestDeliveryScopes.Fixed(bus, new TestRequestActorValidator("expired-token")));

        await runner.RunAsync();

        Assert.Equal(2, handler.LastValue);
    }

    /// <summary>
    ///     Verifies that both delivery paths report the same transport. The trusted-system-actor
    ///     branch (a fired durable schedule) and the token-carrying branch label the same hop, so a
    ///     hardcoded name in one of them would make the reported transport depend on which
    ///     authentication path a delivery happened to take.
    /// </summary>
    [Fact]
    public async Task ShouldReportInvocationTransportOnBothDeliveryPaths()
    {
        // The listener is process-wide and other tests dispatch concurrently, so capture is
        // thread-safe and narrowed to the schedule transport, which only this test produces.
        var transports = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PortiaTelemetry.SourceName,
            Sample = SampleAllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == PortiaTelemetry.ProcessActivityName &&
                    activity.GetTagItem("portia.transport.name") is string transport
                     && transport == "schedule")
                {
                    transports.Enqueue(transport);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var schedule = new ScheduleInvocation("schedule://test/work/item/run");
        var consumer = new FakeRequestNotificationConsumer([
            new RequestNotification(new ChangeValue(1), null, RequestMetadata.Create(),
                schedule, Actor: RequestActor.System),
            new RequestNotification(new ChangeValue(2), "valid-token", RequestMetadata.Create(),
                schedule)
        ]);
        var runner = new RequestNotificationRunner(consumer,
            RequestDeliveryScopes.Fixed(busHost.Bus, new TestRequestActorValidator()));

        await runner.RunAsync();

        Assert.Equal(2, handler.LastValue);
        Assert.Equal([schedule.TransportName, schedule.TransportName], [.. transports]);
    }

    static ActivitySamplingResult SampleAllData(
        ref ActivityCreationOptions<ActivityContext> options)
        => ActivitySamplingResult.AllData;

    sealed class FakeRequestNotificationConsumer(IReadOnlyList<RequestNotification> items)
        : IRequestNotificationConsumer
    {
        public async IAsyncEnumerable<RequestNotification> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return item;
            }
        }
    }

    sealed record ThrowingChangeValue(int Value) : IRequest;
}
