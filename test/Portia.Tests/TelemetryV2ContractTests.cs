using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Cntryl.Portia;

/// <summary>Locks the version-two request telemetry schema and bounded trace topology.</summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class TelemetryV2ContractTests
{
    /// <summary>Every activity uses the namespaced Portia schema and optional messaging conventions.</summary>
    [Fact]
    public void ShouldEmitExactOrderedTagsForAllActivityKinds()
    {
        using var listener = Listen(out var activities);

        using (var execute = PortiaTelemetry.StartExecute("test.request", "local"))
            PortiaTelemetry.RecordOutcome(execute, true, null);
        using (var send = PortiaTelemetry.StartSend("test.request", "queue", "fitz"))
            PortiaTelemetry.RecordOutcome(send, true, null);
        using (var process = PortiaTelemetry.StartProcess("test.request",
                   new QueueInvocation("queue://secret/route", 2) { MessagingSystem = "fitz" }, null))
            PortiaTelemetry.RecordOutcome(process, true, null);

        var executeActivity = Assert.Single(activities, activity => activity.OperationName == PortiaTelemetry.ExecuteActivityName);
        Assert.All(activities, activity =>
        {
            Assert.Equal(PortiaTelemetry.SourceName, activity.Source.Name);
            Assert.Equal("2.0.0", activity.Source.Version);
        });
        Assert.Equal(ActivityKind.Internal, executeActivity.Kind);
        Assert.Equal(["portia.request.name", "portia.transport.name", "portia.outcome"],
            executeActivity.TagObjects.Select(tag => tag.Key));

        var sendActivity = Assert.Single(activities, activity => activity.OperationName == PortiaTelemetry.SendActivityName);
        Assert.Equal(ActivityKind.Producer, sendActivity.Kind);
        Assert.Equal(["portia.request.name", "portia.transport.name", "messaging.system", "messaging.operation.type", "portia.outcome"],
            sendActivity.TagObjects.Select(tag => tag.Key));
        Assert.Equal("send", sendActivity.GetTagItem("messaging.operation.type"));

        var processActivity = Assert.Single(activities, activity => activity.OperationName == PortiaTelemetry.ProcessActivityName);
        Assert.Equal(ActivityKind.Consumer, processActivity.Kind);
        Assert.Equal(["portia.request.name", "portia.transport.name", "messaging.system", "messaging.operation.type", "portia.outcome"],
            processActivity.TagObjects.Select(tag => tag.Key));
        Assert.Equal("process", processActivity.GetTagItem("messaging.operation.type"));
        Assert.DoesNotContain(activities.SelectMany(activity => activity.TagObjects),
            tag => Equals(tag.Value, "queue://secret/route"));
    }

    /// <summary>Adapters that do not know a messaging technology emit no speculative standard attributes.</summary>
    [Fact]
    public void ShouldOmitMessagingAttributesWhenSystemIsUnknown()
    {
        using var listener = Listen(out var activities);

        using (var send = PortiaTelemetry.StartSend("test.request", "custom", null))
            PortiaTelemetry.RecordOutcome(send, true, null);

        var activity = Assert.Single(activities);
        Assert.Equal(["portia.request.name", "portia.transport.name", "portia.outcome"],
            activity.TagObjects.Select(tag => tag.Key));
        Assert.Null(activity.GetTagItem("messaging.system"));
        Assert.Null(activity.GetTagItem("messaging.operation.type"));
    }

    /// <summary>Asynchronous invocations always start new linked roots while RPC preserves parentage.</summary>
    [Fact]
    public void ShouldApplyInvocationTraceRelationship()
    {
        using var listener = Listen(out var activities);
        var propagated = new RequestTraceContext("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");

        using (PortiaTelemetry.StartProcess("queue.request",
                   new QueueInvocation("queue://secret/route", 4) { MessagingSystem = "fitz" }, propagated))
        {
        }

        using (PortiaTelemetry.StartProcess("rpc.request",
                   new RpcInvocation("rpc://secret/route") { MessagingSystem = "fitz" }, propagated))
        {
        }

        var queue = Assert.Single(activities, activity => Equals(activity.GetTagItem("portia.transport.name"), "queue"));
        Assert.NotEqual(propagated.TraceParent[3..35], queue.TraceId.ToString());
        Assert.Equal(propagated.TraceParent[3..35], Assert.Single(queue.Links).Context.TraceId.ToString());

        var rpc = Assert.Single(activities, activity => Equals(activity.GetTagItem("portia.transport.name"), "rpc"));
        Assert.Equal(propagated.TraceParent[3..35], rpc.TraceId.ToString());
        Assert.Empty(rpc.Links);
    }

    /// <summary>The delivery counter has one closed typed outcome and exactly three dimensions.</summary>
    [Fact]
    public void ShouldRecordTypedDeliveryOutcomeWithExactDimensions()
    {
        using var listener = ListenToDelivery(out var measurements);

        PortiaTelemetry.RecordDelivery("test.request", "queue", RequestDeliveryOutcome.Abandoned);

        var measurement = Assert.Single(measurements);
        Assert.Equal(1, measurement.Value);
        Assert.Equal(["portia.request.name", "portia.transport.name", "portia.outcome"],
            measurement.Tags.Select(tag => tag.Key));
        Assert.Equal("abandoned", measurement.Tags[2].Value);
    }

    /// <summary>An undefined enum value cannot silently widen the delivery-outcome vocabulary.</summary>
    [Fact]
    public void ShouldRejectUndefinedDeliveryOutcome() =>
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PortiaTelemetry.RecordDelivery("test.request", "queue", (RequestDeliveryOutcome)42));

    /// <summary>Fitz RPC emits one send-process-execute chain named by the wire discriminator.</summary>
    [Fact]
    public async Task ShouldEmitExactRpcTopologyUsingGeneratedDiscriminator()
    {
        using var listener = Listen(out var activities);
        var rpc = new InMemoryRpcClient();
        var serializer = TestJson.Serializer(typeof(RpcGetValue));
        using var busHost = TestRequestBus.Create();
        var server = new FitzRpcRequestServer(rpc, busHost.ScopeFactory);
        await using var registration = await server.RegisterAsync<RpcGetValue, int>();
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer);

        var result = await sender.SendAsync<RpcGetValue, int>(new RpcGetValue(), RequestRouteValues.None, null);

        Assert.True(result.IsSuccess);
        var requestActivities = activities.Where(activity =>
            Equals(activity.GetTagItem("portia.request.name"), "test.rpc.get-value")).ToArray();
        var send = Assert.Single(requestActivities, activity => activity.OperationName == PortiaTelemetry.SendActivityName);
        var process = Assert.Single(requestActivities,
            activity => activity.OperationName == PortiaTelemetry.ProcessActivityName);
        var execute = Assert.Single(requestActivities,
            activity => activity.OperationName == PortiaTelemetry.ExecuteActivityName);
        Assert.Equal(send.TraceId, process.TraceId);
        Assert.Equal(send.SpanId, process.ParentSpanId);
        Assert.Equal(process.TraceId, execute.TraceId);
        Assert.Equal(process.SpanId, execute.ParentSpanId);
        Assert.Equal("fitz", send.GetTagItem("messaging.system"));
        Assert.Equal("fitz", process.GetTagItem("messaging.system"));
        Assert.Null(execute.GetTagItem("messaging.system"));
    }

    /// <summary>Repeated schedule firings are distinct linked roots with one execute child each.</summary>
    [Fact]
    public async Task ShouldStartDistinctLinkedTraceForEveryScheduleFiring()
    {
        using var listener = Listen(out var activities);
        var propagated = new RequestTraceContext("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        var invocation = new ScheduleInvocation("schedule://secret/route") { MessagingSystem = "fitz" };
        var handler = new UniversalActionHandler();
        using var busHost = TestRequestBus.Create(universalActionHandler: handler);
        var runner = new RequestNotificationRunner(new Notifications(
        [
            new RequestNotification(new UniversalAction(1), null, RequestMetadata.Create(), invocation, propagated,
                RequestActor.System, "test.shared.universal-action"),
            new RequestNotification(new UniversalAction(2), null, RequestMetadata.Create(), invocation, propagated,
                RequestActor.System, "test.shared.universal-action")
        ]), RequestDeliveryScopes.Fixed(busHost.Bus, new TestRequestActorValidator()));

        await runner.RunAsync();

        var requestActivities = activities.Where(activity =>
            Equals(activity.GetTagItem("portia.request.name"), "test.shared.universal-action")).ToArray();
        var processes = requestActivities.Where(activity => activity.OperationName == PortiaTelemetry.ProcessActivityName)
            .ToArray();
        var executions = requestActivities.Where(activity => activity.OperationName == PortiaTelemetry.ExecuteActivityName)
            .ToArray();
        Assert.Equal(2, processes.Length);
        Assert.Equal(2, executions.Length);
        Assert.Equal(2, processes.Select(activity => activity.TraceId).Distinct().Count());
        Assert.All(processes, activity => Assert.Equal(propagated.TraceParent[3..35],
            Assert.Single(activity.Links).Context.TraceId.ToString()));
        Assert.All(processes, process => Assert.Single(executions,
            execute => execute.TraceId == process.TraceId && execute.ParentSpanId == process.SpanId));
    }

    /// <summary>Actor validation closes the process span without inventing an execute child.</summary>
    [Fact]
    public async Task ShouldCompleteProcessWithoutExecuteGivenActorValidationFailure()
    {
        using var listener = Listen(out var activities);
        using var busHost = TestRequestBus.Create();
        var delivery = new RequestDelivery("test.change-value",
            new QueueInvocation("queue://secret/route", 1) { MessagingSystem = "fitz" }, RequestMetadata.Create(),
            "expired-token");

        var result = await RequestDispatch.SendAsync(new TestRequestActorValidator("expired-token"), busHost.Bus,
            new ChangeValue(1), delivery);

        Assert.False(result.WasDispatched);
        var process = Assert.Single(activities,
            activity => activity.OperationName == PortiaTelemetry.ProcessActivityName &&
                        Equals(activity.GetTagItem("portia.request.name"), "test.change-value"));
        Assert.Equal("unauthorized", process.GetTagItem("portia.outcome"));
        Assert.Equal(ActivityStatusCode.Error, process.Status);
        Assert.DoesNotContain(activities, activity => activity.OperationName == PortiaTelemetry.ExecuteActivityName &&
                                                     activity.TraceId == process.TraceId);
        Assert.Empty(process.Events);
    }

    /// <summary>Requested cancellation is non-error; unexpected faults retain the full exception event.</summary>
    [Fact]
    public void ShouldDistinguishRequestedCancellationFromUnexpectedFault()
    {
        using var listener = Listen(out var activities);
        var exception = new InvalidOperationException("operator diagnostic");

        using (var canceled = PortiaTelemetry.StartExecute("test.canceled", "local"))
            PortiaTelemetry.RecordCanceled(canceled);
        using (var faulted = PortiaTelemetry.StartExecute("test.faulted", "local"))
            PortiaTelemetry.RecordFault(faulted, exception);

        var canceledActivity = Assert.Single(activities,
            activity => Equals(activity.GetTagItem("portia.request.name"), "test.canceled"));
        Assert.Equal("canceled", canceledActivity.GetTagItem("portia.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, canceledActivity.Status);
        Assert.Empty(canceledActivity.Events);

        var faultedActivity = Assert.Single(activities,
            activity => Equals(activity.GetTagItem("portia.request.name"), "test.faulted"));
        Assert.Equal("fault", faultedActivity.GetTagItem("portia.outcome"));
        Assert.Equal(ActivityStatusCode.Error, faultedActivity.Status);
        var error = Assert.Single(faultedActivity.Events);
        Assert.Equal("exception", error.Name);
        Assert.Contains(error.Tags, tag => tag.Key == "exception.message" && Equals(tag.Value, exception.Message));
    }

    /// <summary>A persisted schedule links later firings to its send span, not its caller.</summary>
    [Fact]
    public async Task ShouldPersistScheduleProducerContext()
    {
        using var listener = Listen(out var activities);
        var schedule = new CapturingScheduleClient();
        var serializer = TestJson.Serializer(typeof(UniversalAction));

        _ = await new FitzRequestScheduler(schedule, serializer).ScheduleAsync(
            new UniversalAction(7), new RequestScheduleSpec("0 0 * * *"), RequestRouteValues.None,
            RequestActor.CreateSystem("scheduler"));

        var producer = Assert.Single(activities,
            activity => activity.OperationName == PortiaTelemetry.SendActivityName &&
                        Equals(activity.GetTagItem("portia.transport.name"), "schedule"));
        var persisted = JsonSerializer.Deserialize(schedule.Payload.Span,
            FitzJsonContext.Default.FitzScheduledRequestEnvelope)!;
        var envelope = serializer.DeserializeEnvelope(persisted.RequestEnvelope);
        Assert.Equal(producer.Id, envelope.TraceContext?.TraceParent);
        Assert.Equal(producer.TraceStateString, envelope.TraceContext?.TraceState);
    }

    /// <summary>Invalid propagation is ignored and counted without echoing the bad value.</summary>
    [Fact]
    public void ShouldCountAndIgnoreInvalidPropagatedContext()
    {
        using var activityListener = Listen(out var activities);
        var measurements = new ConcurrentBag<Measurement>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == PortiaTelemetry.SourceName &&
                    instrument.Name == "portia.transport.trace_context.invalid")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            measurements.Add(new Measurement(value, tags.ToArray())));
        meterListener.Start();

        using (PortiaTelemetry.StartProcess("test.request", new QueueInvocation("queue://secret", 1),
                   new RequestTraceContext("not-a-trace-parent")))
        {
        }

        var process = Assert.Single(activities);
        Assert.Equal(default, process.ParentSpanId);
        Assert.Empty(process.Links);
        var invalid = Assert.Single(measurements);
        Assert.Equal(1, invalid.Value);
        Assert.Equal(["portia.transport.name"], invalid.Tags.Select(tag => tag.Key));
        Assert.Equal("queue", invalid.Tags[0].Value);
        Assert.DoesNotContain(invalid.Tags, tag => Equals(tag.Value, "not-a-trace-parent"));
    }

    /// <summary>Propagation-only sampling retains status but suppresses tags and exception events.</summary>
    [Fact]
    public void ShouldGateTagsAndExceptionEventsOnRequestedData()
    {
        var activities = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PortiaTelemetry.SourceName,
            Sample = static (ref _) => ActivitySamplingResult.PropagationData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        using (var activity = PortiaTelemetry.StartExecute("secret-request", "local"))
        {
            PortiaTelemetry.RecordFault(activity, new InvalidOperationException("secret-message"));
        }

        var captured = Assert.Single(activities);
        Assert.Equal(ActivityStatusCode.Error, captured.Status);
        Assert.Empty(captured.TagObjects);
        Assert.Empty(captured.Events);
    }

    /// <summary>A cancellation exception without a requested token is an unexpected process fault.</summary>
    [Fact]
    public async Task ShouldTreatUnrequestedCancellationAsFault()
    {
        using var listener = Listen(out var activities);
        using var busHost = TestRequestBus.Create();
        var delivery = new RequestDelivery("test.change-value", new QueueInvocation("queue://secret", 1),
            RequestMetadata.Create());

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => RequestDispatch.SendAsync(
            new UnrequestedCancellationValidator(), busHost.Bus, new ChangeValue(1), delivery).AsTask());

        var process = Assert.Single(activities);
        Assert.Equal("fault", process.GetTagItem("portia.outcome"));
        Assert.Equal(ActivityStatusCode.Error, process.Status);
        Assert.Equal("exception", Assert.Single(process.Events).Name);
    }

    /// <summary>A cancellation observed from the supplied token is non-error and has no exception event.</summary>
    [Fact]
    public async Task ShouldTreatRequestedCancellationAsCanceled()
    {
        using var listener = Listen(out var activities);
        using var busHost = TestRequestBus.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var delivery = new RequestDelivery("test.change-value", new QueueInvocation("queue://secret", 1),
            RequestMetadata.Create());

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => RequestDispatch.SendAsync(
            new CooperativeCancellationValidator(), busHost.Bus, new ChangeValue(1), delivery, cancellation.Token)
            .AsTask());

        var process = Assert.Single(activities);
        Assert.Equal("canceled", process.GetTagItem("portia.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, process.Status);
        Assert.Empty(process.Events);
    }

    static ActivityListener Listen(out ConcurrentBag<Activity> activities)
    {
        var captured = new ConcurrentBag<Activity>();
        activities = captured;
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PortiaTelemetry.SourceName,
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Add
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    static MeterListener ListenToDelivery(out ConcurrentBag<Measurement> measurements)
    {
        var captured = new ConcurrentBag<Measurement>();
        measurements = captured;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == PortiaTelemetry.SourceName &&
                    instrument.Name == "portia.request.delivery.count")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            captured.Add(new Measurement(value, tags.ToArray())));
        listener.Start();
        return listener;
    }

    readonly record struct Measurement(long Value, KeyValuePair<string, object?>[] Tags);

    sealed class Notifications(IReadOnlyList<RequestNotification> notifications) : IRequestNotificationConsumer
    {
        public async IAsyncEnumerable<RequestNotification> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var notification in notifications)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return notification;
            }
        }
    }

    sealed class CapturingScheduleClient : IScheduleClient
    {
        public ReadOnlyMemory<byte> Payload { get; private set; }

        public Task<string?> CreateAsync(string route, string cron, ScheduleDeliveryMode mode,
            ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            Payload = payload;
            return Task.FromResult<string?>("schedule-id");
        }

        public Task CancelAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ScheduleListPage> ListAsync(ulong? offset, ulong? limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ScheduleEntry>> ListBySelectorAsync(string selector,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ScheduleSubscription> SubscribeAsync(string selector, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    sealed class UnrequestedCancellationValidator : IRequestActorValidator
    {
        public ValueTask<Result<System.Security.Claims.ClaimsPrincipal>> ValidateAsync(string? token,
            CancellationToken ct = default) => throw new OperationCanceledException("unexpected cancellation");
    }

    sealed class CooperativeCancellationValidator : IRequestActorValidator
    {
        public ValueTask<Result<System.Security.Claims.ClaimsPrincipal>> ValidateAsync(string? token,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Result<System.Security.Claims.ClaimsPrincipal>.Success(RequestActor.System));
        }
    }
}
