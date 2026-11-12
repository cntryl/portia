using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
///     Verifies that queued requests dispatch through the same request bus as any other origin.
/// </summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class QueueRunnerTests
{
    /// <summary>Applies the configured delay consistently to every incomplete delivery path.</summary>
    /// <param name="situation">The incomplete processing outcome.</param>
    [Theory]
    [InlineData("transient")]
    [InlineData("permanent")]
    [InlineData("exception")]
    [InlineData("read")]
    [InlineData("actor")]
    [InlineData("transport")]
    public async Task ShouldApplyRetryDelayGivenConfiguredDelayAndIncompleteDeliveries(string situation)
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = situation switch
        {
            "transient" => new FakeQueuedRequest(new InvalidChangeValue(1, true)),
            "permanent" => new FakeQueuedRequest(new InvalidChangeValue(1)),
            "exception" => new FakeQueuedRequest(new ChangeValue(1), throwOnDispatch: true),
            "read" => new FakeQueuedRequest(new ChangeValue(1), readException: new JsonException("Invalid envelope.")),
            "actor" => new FakeQueuedRequest(new ChangeValue(1), actorToken: "expired-token"),
            _ => new FakeQueuedRequest(new ChangeValue(1), transportMismatch: true)
        };
        var options = new QueueRunnerOptions { RetryDelay = TimeSpan.FromSeconds(7) };
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator("expired-token"), options: options));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Equal(1, queued.Retries);
        Assert.Equal(options.RetryDelay, queued.RetryDelay);
        Assert.Equal(0, queued.AbandonmentCount);
        Assert.Equal(0, queued.CompletionCount);
    }

    /// <summary>A failed release cannot enter the processing-failure path and release the same reservation twice.</summary>
    [Fact]
    public async Task ShouldNotRetryDispositionGivenNativeRetryFails()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new InvalidChangeValue(1, true), throwOnRetry: true);

        // Act
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), options: new QueueRunnerOptions { RetryDelay = TimeSpan.Zero }));

        // Assert
        _ = await Assert.ThrowsAsync<IOException>(() => runner.RunAsync());

        Assert.Equal(1, queued.Retries);
        Assert.Equal(0, queued.AbandonmentCount);
        Assert.Equal(0, queued.CompletionCount);
    }

    /// <summary>Rejects delays the wire cannot represent before reading any queued request.</summary>
    /// <param name="milliseconds">The invalid delay.</param>
    [Theory]
    [InlineData(-1000)]
    [InlineData(1500)]
    public async Task ShouldRejectBeforeProcessingGivenInvalidRetryDelay(int milliseconds)
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1));

        // Act
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), options: new QueueRunnerOptions
            { RetryDelay = TimeSpan.FromMilliseconds(milliseconds) }));

        // Assert
        _ = await Assert.ThrowsAsync<QueueConfigurationException>(() => runner.RunAsync());

        Assert.Equal(0, queued.InvocationReadCount);
        Assert.Equal(0, queued.CompletionCount);
        Assert.Equal(0, queued.Retries);
    }

    /// <summary>Explicit retry configuration cannot release a lost or already acknowledged reservation.</summary>
    /// <param name="lost">Whether the reservation was lost instead of the acknowledgment failing.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldNotRetryGivenLostReservationOrFailedAcknowledgment(bool lost)
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        using var reservation = new CancellationTokenSource();
        if (lost)
            reservation.Cancel();
        var queued = new FakeQueuedRequest(new ChangeValue(1), throwOnAcknowledge: !lost,
            reservation: reservation.Token);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), options: new QueueRunnerOptions { RetryDelay = TimeSpan.Zero }));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Equal(0, queued.Retries);
        Assert.Equal(0, queued.AbandonmentCount);
        Assert.Equal(lost ? 0 : 1, queued.CompletionCount);
    }

    /// <summary>An adapter cannot silently ignore an explicitly configured redelivery delay.</summary>
    [Fact]
    public async Task ShouldRejectConfiguredRetryDelayGivenAdapterDoesNotSupportIt()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var original = new FakeQueuedRequest(new InvalidChangeValue(1, true));

        // Act
        var runner = new QueueRunner(new FakeQueueConsumer([new LegacyQueuedRequest(original)]),
            RequestDeliveryScopes.FixedQueue(busHost.Bus, new TestRequestActorValidator(),
                options: new QueueRunnerOptions { RetryDelay = TimeSpan.FromSeconds(7) }));

        // Assert
        _ = await Assert.ThrowsAsync<NotSupportedException>(() => runner.RunAsync());

        Assert.Equal(0, original.Retries);
        Assert.Equal(0, original.AbandonmentCount);
        Assert.Equal(0, original.CompletionCount);
    }

    /// <summary>Redelivery preserves the typed logical key while each worker execution is new.</summary>
    [Fact]
    public async Task ShouldPreserveTypedIdentityAcrossAttemptsGivenQueuedRedelivery()
    {
        // Arrange
        var handler = new RequestIdentityTests.IdentifiedCommandHandler();
        using var provider = RequestIdentityTests.Compose(new RequestIdentityTests.IdentifiedQueryHandler(),
            handler, new RequestIdentityTests.IdentityAuthorizer());
        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
        var request = new RequestIdentityTests.IdentifiedCommand(Uuid.CreateVersion4());
        var metadata = RequestMetadata.FromRequest(request);
        var items = new[] { new FakeQueuedRequest(request, metadata: metadata),
            new FakeQueuedRequest(request, attempt: 2, metadata: metadata) };
        var runner = new QueueRunner(new FakeQueueConsumer(items), RequestDeliveryScopes.FixedQueue(
            bus, new TestRequestActorValidator(), terminalHandler: new RecordingTerminalHandler()));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.All(items, item => Assert.True(item.Completed));
        Assert.Equal(2, handler.Contexts.Count);
        Assert.All(handler.Contexts, context => Assert.Equal(request.CommandId, context.RequestId));
        Assert.Equal(2, handler.Contexts.Select(context => context.ExecutionId).Distinct().Count());
    }

    /// <summary>Actual terminal, acknowledgment and fault logs correlate to their delivery process.</summary>
    [Theory]
    [InlineData("terminal", false)]
    [InlineData("terminal", true)]
    [InlineData("ack", false)]
    [InlineData("ack", true)]
    [InlineData("fault", false)]
    [InlineData("fault", true)]
    [InlineData("terminal-ack", false)]
    [InlineData("terminal-ack", true)]
    public async Task ShouldExportCorrelatedOutcomeLogsGivenActualQueueOutcome(string mode, bool ambient)
    {
        // Arrange
        using var capture = new RunnerExportCapture();
        using var caller = ambient ? new Activity("unrelated-caller").Start() : null;
        using var busHost = TestRequestBus.Create(telemetryFailureActionHandler:
            mode == "fault" ? new TelemetryFailureActionHandler(new InvalidOperationException("Controlled handler fault.")) : null);
        var items = Enumerable.Range(1, 2).Select(value => (IQueuedRequest)new FakeQueuedRequest(
            mode is "terminal" or "terminal-ack" ? new InvalidChangeValue(value)
                : mode == "fault" ? new TelemetryFailureAction() : new ChangeValue(value),
            throwOnAcknowledge: mode is "ack" or "terminal-ack",
            traceContext: ambient ? new RequestTraceContext(RunnerExportCapture.Parent) : null)).ToArray();
        var runner = new QueueRunner(new FakeQueueConsumer(items), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), terminalHandler: new RecordingTerminalHandler()),
            capture.Logger<QueueRunner>());

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Same(caller, Activity.Current);
        var terminal = mode is "terminal" or "terminal-ack";
        capture.AssertCorrelated(terminal ? 1005 : 1002,
            terminal ? LogLevel.Warning : LogLevel.Error, 2);
        if (ambient)
            Assert.All(capture.Activities.Where(activity => activity.OperationName == PortiaTelemetry.ProcessActivityName),
                process => Assert.True(Assert.Single(process.Links).Context.IsRemote));
    }

    /// <summary>Simultaneous runners retain distinct delivery roots and restore their shared caller context.</summary>
    [Fact]
    public async Task ShouldKeepLogCorrelationsSeparateGivenConcurrentRunners()
    {
        // Arrange
        using var capture = new RunnerExportCapture();
        using var caller = new Activity("unrelated-concurrent-caller").Start();
        using var busHost = TestRequestBus.Create(telemetryFailureActionHandler:
            new TelemetryFailureActionHandler(new InvalidOperationException("Controlled concurrent fault.")));
        Task RunAsync() => new QueueRunner(new FakeQueueConsumer([new FakeQueuedRequest(new TelemetryFailureAction())]),
            RequestDeliveryScopes.FixedQueue(busHost.Bus, new TestRequestActorValidator(), terminalHandler: new RecordingTerminalHandler()),
            capture.Logger<QueueRunner>()).RunAsync();

        // Act
        await Task.WhenAll(RunAsync(), RunAsync());

        // Assert
        Assert.Same(caller, Activity.Current);
        capture.AssertCorrelated(1002, LogLevel.Error, 2);
    }

    /// <summary>Each reservation records one final disposition and only terminal/fault logs.</summary>
    [Fact]
    public async Task ShouldRecordOneDeliveryOutcomeGivenRetryOrBusinessErrorWithoutLogNoise()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var logger = new CapturingLogger();
        var items = new IQueuedRequest[]
        {
            new FakeQueuedRequest(new ChangeValue(1)),
            new FakeQueuedRequest(new InvalidChangeValue(2, true)),
            new FakeQueuedRequest(new InvalidChangeValue(3)),
            new FakeQueuedRequest(new ChangeValue(4), true)
        };
        var terminal = new RecordingTerminalHandler();
        using var meter = ListenToDeliveries(out var outcomes);
        var runner = new QueueRunner(new FakeQueueConsumer(items), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), terminalHandler: terminal), logger);

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Equal(["abandoned", "abandoned", "completed", "terminal"], outcomes.Order());
        Assert.Equal([1005, 1002], logger.Entries.Select(entry => entry.EventId.Id));
        Assert.DoesNotContain(logger.Entries,
            entry => entry.Message.Contains("Value is invalid", StringComparison.Ordinal));
        Assert.IsType<InvalidOperationException>(logger.Entries[1].Exception);
    }

    /// <summary>
    ///     Verifies that every queued request is dispatched to its handler and completed.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchAndCompleteEveryRequestGivenQueuedRequests()
    {
        // Arrange
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var bus = busHost.Bus;
        var items = new[]
        {
            new FakeQueuedRequest(new ChangeValue(1)),
            new FakeQueuedRequest(new ChangeValue(2)),
            new FakeQueuedRequest(new ChangeValue(3))
        };
        var consumer = new FakeQueueConsumer(items);
        var runner = new QueueRunner(consumer, RequestDeliveryScopes.FixedQueue(bus, new TestRequestActorValidator(),
            terminalHandler: new RecordingTerminalHandler()));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Equal(3, handler.LastValue);
        Assert.All(items, item => Assert.True(item.Completed));
        Assert.All(items, item => Assert.False(item.Abandoned));
    }

    /// <summary>
    ///     Verifies that a failed dispatch abandons the request instead of stopping the run, and
    ///     still dispatches the requests that follow it.
    /// </summary>
    [Fact]
    public async Task ShouldAbandonAndContinueGivenFailedRequest()
    {
        // Arrange
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var bus = busHost.Bus;
        var failing = new FakeQueuedRequest(new ChangeValue(1), true);
        var succeeding = new FakeQueuedRequest(new ChangeValue(2));
        var consumer = new FakeQueueConsumer([failing, succeeding]);
        var runner = new QueueRunner(consumer, RequestDeliveryScopes.FixedQueue(bus, new TestRequestActorValidator(),
            terminalHandler: new RecordingTerminalHandler()));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.False(failing.Completed);
        Assert.True(failing.Abandoned);
        Assert.True(succeeding.Completed);
        Assert.Equal(2, handler.LastValue);
    }

    /// <summary>
    ///     Verifies that a non-transient <see cref="RequestError" /> is reported then left to the transport.
    /// </summary>
    [Fact]
    public async Task ShouldLeaveForTransportRedeliveryGivenNonTransientFailureAfterCallback()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var bus = busHost.Bus;
        var invalid = new FakeQueuedRequest(new InvalidChangeValue(1));
        var terminal = new RecordingTerminalHandler();
        var consumer = new FakeQueueConsumer([invalid]);
        var runner = new QueueRunner(consumer,
            RequestDeliveryScopes.FixedQueue(bus, new TestRequestActorValidator(), terminalHandler: terminal));

        // Act
        await runner.RunAsync();

        // Assert
        var failure = Assert.Single(terminal.Failures);
        Assert.Equal(QueuedRequestTerminalReason.PermanentFailure, failure.Reason);
        Assert.Same(invalid.Request, failure.Request);
        Assert.Same(invalid.Metadata, failure.Metadata);
        Assert.Equal(invalid.Invocation, failure.Invocation);
        Assert.Equal(invalid.Attempt, failure.Attempt);
        Assert.Equal("Value is invalid.", failure.Error?.Message);
        Assert.Null(failure.Exception);
        Assert.False(invalid.Completed);
        Assert.True(invalid.Abandoned);
    }

    /// <summary>A direct runner abandons a terminal failure when no application observer is registered.</summary>
    [Fact]
    public async Task ShouldAbandonGivenTerminalFailureAndNoOptionalHandler()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new InvalidChangeValue(1));
        var consumer = new FakeQueueConsumer([queued]);
        var runner = new QueueRunner(consumer,
            RequestDeliveryScopes.FixedQueue(busHost.Bus, new TestRequestActorValidator()));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Equal(1, consumer.EnumerationCount);
        Assert.False(queued.Completed);
        Assert.True(queued.Abandoned);
    }

    /// <summary>A success-only queue does not need an unused terminal observer.</summary>
    [Fact]
    public async Task ShouldRunQueueGivenSuccessAndNoOptionalHandler()
    {
        // Arrange
        using var busHost = TestRequestBus.Create(new ChangeValueHandler());
        var queued = new FakeQueuedRequest(new ChangeValue(1));
        var runner = new QueueRunner(new FakeQueueConsumer([queued]),
            RequestDeliveryScopes.FixedQueue(busHost.Bus, new TestRequestActorValidator()));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.True(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>
    ///     Verifies that a request whose carried actor token fails re-validation is reported and left
    ///     to the transport without reaching the handler.
    /// </summary>
    [Fact]
    public async Task ShouldLeaveRequestForTransportGivenActorTokenFailsRevalidation()
    {
        // Arrange
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var bus = busHost.Bus;
        var expired = new FakeQueuedRequest(new ChangeValue(1), actorToken: "expired-token");
        var terminal = new RecordingTerminalHandler();
        var consumer = new FakeQueueConsumer([expired]);
        var runner = new QueueRunner(consumer,
            RequestDeliveryScopes.FixedQueue(bus, new TestRequestActorValidator("expired-token"),
                terminalHandler: terminal));

        // Act
        await runner.RunAsync();

        // Assert
        var failure = Assert.Single(terminal.Failures);
        Assert.Equal(QueuedRequestTerminalReason.ActorValidationFailure, failure.Reason);
        Assert.Equal(RequestErrorKind.Unauthorized, failure.Error?.Kind);
        Assert.Null(failure.Exception);
        Assert.False(expired.Completed);
        Assert.True(expired.Abandoned);
        Assert.Null(handler.LastValue);
    }

    /// <summary>
    ///     Verifies that a request whose actor token re-validation fails does not stop the run — the
    ///     requests that follow it still dispatch normally.
    /// </summary>
    [Fact]
    public async Task ShouldContinueGivenActorTokenRevalidationFailure()
    {
        // Arrange
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var bus = busHost.Bus;
        var expired = new FakeQueuedRequest(new ChangeValue(1), actorToken: "expired-token");
        var valid = new FakeQueuedRequest(new ChangeValue(2), actorToken: "valid-token");
        var terminal = new RecordingTerminalHandler();
        var consumer = new FakeQueueConsumer([expired, valid]);
        var runner = new QueueRunner(consumer,
            RequestDeliveryScopes.FixedQueue(bus, new TestRequestActorValidator("expired-token"),
                terminalHandler: terminal));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.False(expired.Completed);
        Assert.True(expired.Abandoned);
        Assert.True(valid.Completed);
        Assert.Equal(2, handler.LastValue);
    }

    /// <summary>Callback completion precedes returning a terminal delivery to its transport.</summary>
    [Fact]
    public async Task ShouldAbandonExactlyOnceGivenTerminalCallback()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var operations = new List<string>();
        var queued = new FakeQueuedRequest(new InvalidChangeValue(1), operations: operations);
        var terminal = new RecordingTerminalHandler(operations: operations);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), terminalHandler: terminal));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Equal(["terminal", "abandon"], operations);
        Assert.Equal(0, queued.CompletionCount);
        Assert.Equal(1, queued.AbandonmentCount);
    }

    /// <summary>The transport invocation is snapshot data through terminal callback and abandonment.</summary>
    [Fact]
    public async Task ShouldReadInvocationOnceGivenTerminalCallbackAndAbandonment()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new InvalidChangeValue(1), invocationMayBeReadOnce: true);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), terminalHandler: terminal));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Equal(1, queued.InvocationReadCount);
        _ = Assert.Single(terminal.Failures);
        Assert.Equal(1, queued.AbandonmentCount);
    }

    /// <summary>
    ///     Verifies that a terminal delivery never attempts acknowledgment and later deliveries continue.
    /// </summary>
    [Fact]
    public async Task ShouldContinueAndLeaveWithTransportGivenTerminalDelivery()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var faults = new List<KeyValuePair<string, object?>[]>();
        using var deliveryMeter = ListenToDeliveries(out var deliveryOutcomes);
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PortiaTelemetry.SourceName && instrument.Name == "portia.worker.failure")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) => faults.Add(tags.ToArray()));
        meterListener.Start();
        var refused = new FakeQueuedRequest(new InvalidChangeValue(1), throwOnAcknowledge: true);
        var later = new FakeQueuedRequest(new ChangeValue(7));
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([refused, later]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), terminalHandler: terminal));

        // Act
        await runner.RunAsync();

        // Assert
        _ = Assert.Single(terminal.Failures);
        Assert.Equal(0, refused.CompletionCount);
        Assert.Equal(1, refused.AbandonmentCount);
        Assert.True(later.Completed);
        meterListener.Dispose();
        Assert.Empty(faults);
        Assert.Equal(["completed", "terminal"], deliveryOutcomes.Order());
    }

    /// <summary>
    ///     A request its handler completed is never dead-lettered, even at the terminal attempt: a refused
    ///     acknowledgment is a cleanup fault, and the transport's redelivery is the at-least-once path.
    /// </summary>
    [Fact]
    public async Task ShouldNotDeadLetterGivenSuccessfulRequestWhoseAcknowledgmentFailsAtTerminalAttempt()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        using var deliveryMeter = ListenToDeliveries(out var deliveryOutcomes);
        var queued = new FakeQueuedRequest(new ChangeValue(5), attempt: 3, throwOnAcknowledge: true);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }, terminal));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Empty(terminal.Failures);
        Assert.Equal(1, queued.CompletionCount);
        Assert.False(queued.Abandoned);
        Assert.Equal(["fault"], deliveryOutcomes);
    }

    /// <summary>
    ///     A delivery whose reservation was lost during dispatch belongs to the transport again, which will
    ///     redeliver it, so the runner neither dead-letters nor abandons it.
    /// </summary>
    [Fact]
    public async Task ShouldLeaveDeliveryGivenReservationLostToTransport()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        using var deliveryMeter = ListenToDeliveries(out var deliveryOutcomes);
        using var lost = new CancellationTokenSource();
        await lost.CancelAsync();
        var queued = new FakeQueuedRequest(new ChangeValue(5), true, attempt: 3, reservation: lost.Token);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }, terminal));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Empty(terminal.Failures);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
        Assert.Equal(["fault"], deliveryOutcomes);
    }

    /// <summary>
    ///     A reservation lost while the handler runs leaves the delivery with the transport whatever the
    ///     handler returned; the transport that gave the reservation up reports why.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldLeaveDeliveryGivenReservationLostWhileHandlerRuns(bool fails)
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        using var deliveryMeter = ListenToDeliveries(out var deliveryOutcomes);
        using var lost = new CancellationTokenSource();
        var logger = new CapturingLogger();
        var queued = new FakeQueuedRequest(fails ? new InvalidChangeValue(5) : new ChangeValue(5), attempt: 3,
            reservation: lost.Token);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            new AfterDispatchBus(busHost.Bus, lost.Cancel), new TestRequestActorValidator(),
            new QueueRunnerOptions { TerminalAttempt = 3 }, terminal), logger);

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Empty(terminal.Failures);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
        Assert.Empty(logger.Entries);
        Assert.Equal(["fault"], deliveryOutcomes);
    }

    /// <summary>
    ///     Stopping the host cancels a transport's reservation token with it, which is a shutdown and
    ///     not a lost reservation: the handled delivery is still acknowledged and nothing is reported.
    /// </summary>
    [Fact]
    public async Task ShouldTreatAsCancellationGivenHostShutdownAfterDispatch()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        using var deliveryMeter = ListenToDeliveries(out var deliveryOutcomes);
        using var host = new CancellationTokenSource();
        using var reservation = CancellationTokenSource.CreateLinkedTokenSource(host.Token);
        var logger = new CapturingLogger();
        var queued = new FakeQueuedRequest(new ChangeValue(5), reservation: reservation.Token);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            new AfterDispatchBus(busHost.Bus, host.Cancel), new TestRequestActorValidator(),
            terminalHandler: new RecordingTerminalHandler()), logger);

        // Act
        await runner.RunAsync(host.Token);

        // Assert
        Assert.True(queued.Completed);
        Assert.Empty(logger.Entries);
        Assert.Equal(["completed"], deliveryOutcomes);
    }

    /// <summary>A retryable handler result at the configured threshold becomes terminal.</summary>
    [Fact]
    public async Task ShouldInvokeTerminalHandlerGivenRetryableResultAtTerminalAttempt()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new InvalidChangeValue(1, true), attempt: 3);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }, terminal));

        // Act
        await runner.RunAsync();

        // Assert
        var failure = Assert.Single(terminal.Failures);
        Assert.Equal(QueuedRequestTerminalReason.RetryLimitReached, failure.Reason);
        Assert.True(failure.Error?.IsTransient);
        Assert.Null(failure.Exception);
        Assert.False(queued.Completed);
        Assert.True(queued.Abandoned);
    }

    /// <summary>An unknown wire contract remains transport-owned below its durable threshold.</summary>
    [Fact]
    public async Task ShouldAbandonGivenRetryableEnvelopeFailureBelowTerminalAttempt()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), attempt: 2,
            readException: RequestEnvelopeFailure.Create(RequestEnvelopeFailureKind.Retryable, "unknown contract"));
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }, terminal));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Empty(terminal.Failures);
        Assert.True(queued.Abandoned);
        Assert.False(queued.Completed);
    }

    /// <summary>A retryable read failure reaches the callback at threshold and remains transport-owned.</summary>
    [Fact]
    public async Task ShouldAbandonRetryableEnvelopeFailureAtTerminalAttemptGivenHandler()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), attempt: 3,
            readException: RequestEnvelopeFailure.Create(RequestEnvelopeFailureKind.Retryable, "unknown contract"));
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }, terminal));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Equal(QueuedRequestTerminalReason.RetryLimitReached, Assert.Single(terminal.Failures).Reason);
        Assert.False(queued.Completed);
        Assert.True(queued.Abandoned);
    }

    /// <summary>A permanent lazy read always reaches the terminal callback and later work continues.</summary>
    [Fact]
    public async Task ShouldTerminalizeAndContinueGivenPermanentEnvelopeFailure()
    {
        // Arrange
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var malformed = new FakeQueuedRequest(new ChangeValue(1),
            readException: RequestEnvelopeFailure.Create(RequestEnvelopeFailureKind.Permanent, "malformed"));
        var later = new FakeQueuedRequest(new ChangeValue(7));
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([malformed, later]),
            RequestDeliveryScopes.FixedQueue(busHost.Bus, new TestRequestActorValidator(), terminalHandler: terminal));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Equal(QueuedRequestTerminalReason.DeserializationFailure, Assert.Single(terminal.Failures).Reason);
        Assert.False(malformed.Completed);
        Assert.True(malformed.Abandoned);
        Assert.True(later.Completed);
        Assert.Equal(7, handler.LastValue);
    }

    /// <summary>An abandoned lazy read failure is visible exactly once as an execution fault.</summary>
    [Fact]
    public async Task ShouldRecordExactlyOnceGivenAbandonedReadFailure()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var faults = new List<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PortiaTelemetry.SourceName && instrument.Name == "portia.worker.failure")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => faults.Add(tags.ToArray()));
        listener.Start();
        var queued = new FakeQueuedRequest(new ChangeValue(1), attempt: 2,
            readException: RequestEnvelopeFailure.Create(RequestEnvelopeFailureKind.Retryable, "unknown contract"));
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 },
            new RecordingTerminalHandler()));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.True(queued.Abandoned);
        var fault = Assert.Single(faults);
        Assert.Contains(fault, tag => Equals(tag.Value, nameof(QueueRunner)));
        Assert.Contains(fault, tag => Equals(tag.Value, "execution"));
    }

    /// <summary>A malformed read with an application callback retains terminal classification and broker ownership.</summary>
    [Fact]
    public async Task ShouldAbandonPermanentEnvelopeFailureAsDeserializationFailureGivenHandler()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1),
            readException: RequestEnvelopeFailure.Create(RequestEnvelopeFailureKind.Permanent, "malformed"));
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), terminalHandler: terminal));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Equal(QueuedRequestTerminalReason.DeserializationFailure, Assert.Single(terminal.Failures).Reason);
        Assert.False(queued.Completed);
        Assert.True(queued.Abandoned);
    }

    /// <summary>A retryable lazy read is terminalized at its durable threshold.</summary>
    [Fact]
    public async Task ShouldTerminalizeGivenRetryableEnvelopeFailureAtThreshold()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), attempt: 3,
            readException: RequestEnvelopeFailure.Create(RequestEnvelopeFailureKind.Retryable, "unknown contract"));
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }, terminal));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Equal(QueuedRequestTerminalReason.RetryLimitReached, Assert.Single(terminal.Failures).Reason);
        Assert.True(queued.Abandoned);
        Assert.False(queued.Completed);
    }

    /// <summary>A terminal threshold is rejected by the runner when the adapter has no durable count.</summary>
    [Fact]
    public async Task ShouldRejectTerminalAttemptGivenTransportDoesNotSupportDurableAttempts()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), supportsDurableAttempts: false);

        // Act
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 },
            new RecordingTerminalHandler()));

        // Assert
        var error = await Assert.ThrowsAsync<QueueConfigurationException>(() => runner.RunAsync());

        Assert.Contains("durable delivery-attempt count", error.Message, StringComparison.Ordinal);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>Zero is not a meaningful terminal delivery attempt.</summary>
    [Fact]
    public async Task ShouldRejectBeforeReadingDeliveryGivenZeroTerminalAttempt()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1));

        // Act
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 0 },
            new RecordingTerminalHandler()));

        // Assert
        var error = await Assert.ThrowsAsync<QueueConfigurationException>(() => runner.RunAsync());

        Assert.Contains("must be positive", error.Message, StringComparison.Ordinal);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>
    ///     A terminal-attempt setting the transport cannot honor is a configuration error, so the hosted
    ///     runner stops rather than restarting, and reserving, forever.
    /// </summary>
    [Theory]
    [InlineData(0u, true)]
    [InlineData(3u, false)]
    public async Task ShouldFaultHostedRunnerGivenUnusableTerminalAttempt(uint terminalAttempt, bool durable)
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var consumer = new FakeQueueConsumer([
            new FakeQueuedRequest(new ChangeValue(1), supportsDurableAttempts: durable),
            new FakeQueuedRequest(new ChangeValue(2), supportsDurableAttempts: durable)
        ]);
        var runner = new QueueRunner(consumer, RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = terminalAttempt },
            new RecordingTerminalHandler()));
        using var hosted = new QueueRunnerHostedService(runner);

        // Act
        await hosted.StartAsync(default);

        // Assert
        _ = await Assert.ThrowsAsync<QueueConfigurationException>(async () =>
            await (hosted.ExecuteTask ?? throw new InvalidOperationException("The hosted runner did not start."))
                .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>A request that did not declare queue delivery is terminal, never dispatched, and returned to transport.</summary>
    [Fact]
    public async Task ShouldAbandonAsInvalidTransportGivenQueueCapabilityMismatch()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), transportMismatch: true);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), terminalHandler: terminal));

        // Act
        await runner.RunAsync();

        // Assert
        var failure = Assert.Single(terminal.Failures);
        Assert.Equal(QueuedRequestTerminalReason.InvalidTransport, failure.Reason);
        _ = Assert.IsType<InvalidRequestTransportException>(failure.Exception);
        Assert.False(queued.Completed);
        Assert.True(queued.Abandoned);
    }

    /// <summary>An unexpected exception at the configured threshold also becomes terminal.</summary>
    [Fact]
    public async Task ShouldInvokeTerminalHandlerGivenUnexpectedFailureAtTerminalAttempt()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), true, attempt: 3);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }, terminal));

        // Act
        await runner.RunAsync();

        // Assert
        var failure = Assert.Single(terminal.Failures);
        Assert.Equal((uint)3, failure.Attempt);
        Assert.Equal(QueuedRequestTerminalReason.RetryLimitReached, failure.Reason);
        Assert.NotNull(failure.Exception);
        Assert.False(queued.Completed);
        Assert.True(queued.Abandoned);
    }

    /// <summary>A retryable result remains broker-owned until a configured threshold is reached.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(3U)]
    public async Task ShouldAbandonGivenRetryableResultBelowThresholdOrNoThreshold(uint? terminalAttempt)
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new InvalidChangeValue(1, true), attempt: 2);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = terminalAttempt },
            terminal));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Empty(terminal.Failures);
        Assert.False(queued.Completed);
        Assert.True(queued.Abandoned);
    }

    /// <summary>A terminal callback failure leaves ownership with the transport.</summary>
    [Fact]
    public async Task ShouldLeaveDeliveryUnacknowledgedGivenTerminalHandlerFailureWhenAttemptIsTerminal()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), true, attempt: 3);

        // Act
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 },
            new RecordingTerminalHandler(true)));

        // Assert
        var failure = await Assert.ThrowsAsync<TerminalHandlerFailureException>(() => runner.RunAsync());

        Assert.Equal("terminal failed", failure.InnerException?.Message);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>The hosted transport must not turn a terminal callback fault into a restart loop.</summary>
    [Fact]
    public async Task ShouldFaultHostedRunnerGivenTerminalHandlerFailure()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), true, attempt: 3);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 },
            new RecordingTerminalHandler(true)));
        using var hosted = new QueueRunnerHostedService(runner);

        // Act
        await hosted.StartAsync(default);

        // Assert
        var failure = await Assert.ThrowsAsync<TerminalHandlerFailureException>(async () =>
            await (hosted.ExecuteTask ?? throw new InvalidOperationException("The hosted runner did not start.")));

        Assert.Equal("terminal failed", failure.InnerException?.Message);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>A configured retry threshold remains transport-owned without an optional terminal observer.</summary>
    [Fact]
    public async Task ShouldAbandonAtRetryLimitGivenNoTerminalHandler()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), true, attempt: 3);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.False(queued.Completed);
        Assert.True(queued.Abandoned);
    }

    /// <summary>Failures remain retryable below the configured terminal threshold.</summary>
    [Fact]
    public async Task ShouldAbandonGivenFailureBelowRetryLimitAndMissingHandler()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), true, attempt: 2);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 },
            new RecordingTerminalHandler()));

        // Act
        await runner.RunAsync();

        // Assert
        Assert.False(queued.Completed);
        Assert.True(queued.Abandoned);
    }

    /// <summary>A hosted queue can run without registering a terminal observer.</summary>
    [Fact]
    public async Task ShouldRunHostedQueueGivenNoTerminalHandler()
    {
        // Arrange
        using var busHost = TestRequestBus.Create(new ChangeValueHandler());
        var queued = new FakeQueuedRequest(new ChangeValue(1));
        var consumer = new OneThenWaitQueueConsumer(queued);
        var runner = new QueueRunner(consumer,
            RequestDeliveryScopes.FixedQueue(busHost.Bus, new TestRequestActorValidator()));
        using var hosted = new QueueRunnerHostedService(runner);

        await hosted.StartAsync(default);
        await consumer.Waiting.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        await hosted.StopAsync(default);

        // Assert
        Assert.True(queued.Completed);
        Assert.Equal(1, queued.CompletionCount);
        Assert.False(queued.Abandoned);
    }

    /// <summary>Fitz's combined worker host must propagate both terminal ownership faults.</summary>
    [Fact]
    public async Task ShouldFaultFitzWorkerRestartBoundaryGivenTerminalCallbackFailure()
    {
        // Arrange
        var expected = new TerminalHandlerFailureException(new InvalidOperationException("terminal failed"));

        // Act
        var attempts = 0;

        // Assert
        var failure = await Assert.ThrowsAsync(expected.GetType(), () => FitzApplicationWorkers.RetryAsync(
            "FitzQueueWorkerDefinition", _ =>
            {
                attempts++;
                return Task.FromException(expected);
            }, TimeSpan.FromSeconds(1), TimeProvider.System, null, default));

        Assert.Same(expected, failure);
        Assert.Equal(1, attempts);
    }

    /// <summary>Fitz does not restart a queue with an invalid terminal-policy configuration.</summary>
    [Fact]
    public async Task ShouldFaultFitzWorkerRestartBoundaryGivenQueueConfigurationFailure()
    {
        // Arrange
        var expected = new QueueConfigurationException("Invalid queue runner configuration.");

        // Act
        var attempts = 0;

        // Assert
        var failure = await Assert.ThrowsAsync<QueueConfigurationException>(() => FitzApplicationWorkers.RetryAsync(
            "FitzQueueWorkerDefinition", _ =>
            {
                attempts++;
                return Task.FromException(expected);
            }, TimeSpan.FromSeconds(1), TimeProvider.System, null, default));

        Assert.Same(expected, failure);
        Assert.Equal(1, attempts);
    }

    /// <summary>Preflight scope dependencies are disposed and delivery uses a fresh scope.</summary>
    [Fact]
    public async Task ShouldDisposePreflightScopeAndUseFreshDeliveryScopeGivenDelivery()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var scopes = new RecordingQueueScopeFactory(busHost.Bus, new TestRequestActorValidator(),
            _ => true);
        var queued = new FakeQueuedRequest(new ChangeValue(1));

        // Act
        await new QueueRunner(new FakeQueueConsumer([queued]), scopes).RunAsync();

        // Assert
        Assert.Equal(2, scopes.Created.Count);
        Assert.All(scopes.Created, scope => Assert.True(scope.Disposed));
        Assert.NotSame(scopes.Created[0].TerminalHandler, scopes.Created[1].TerminalHandler);
        Assert.True(queued.Completed);
    }

    /// <summary>A terminal observer may be absent from an individual delivery scope.</summary>
    [Fact]
    public async Task ShouldAllowDeliveryScopeGivenNoOptionalTerminalObserver()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var scopes = new RecordingQueueScopeFactory(busHost.Bus, new TestRequestActorValidator(),
            index => index == 0);
        var queued = new FakeQueuedRequest(new ChangeValue(1));

        // Act
        await new QueueRunner(new FakeQueueConsumer([queued]), scopes).RunAsync();

        // Assert
        Assert.Equal(2, scopes.Created.Count);
        Assert.All(scopes.Created, scope => Assert.True(scope.Disposed));
        Assert.True(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>B1-B12: Queue read failures preserve disposition, continuation, telemetry, and ownership invariants.</summary>
    [Theory]
    [MemberData(nameof(QueueReadFailureCases))]
    public async Task ShouldApplyQueueReadFailurePolicyGivenMatrixCell(string cellId, string failureKind,
        uint? terminalAttempt, uint attempt, bool terminal, bool faultLogged, bool throwInvocation)
    {
        // Arrange
        using var busHost = TestRequestBus.Create(new ChangeValueHandler());
        Exception exception = failureKind switch
        {
            "permanent" => RequestEnvelopeFailure.Create(RequestEnvelopeFailureKind.Permanent, cellId),
            "retryable" => RequestEnvelopeFailure.Create(RequestEnvelopeFailureKind.Retryable, cellId),
            _ => new IOException(cellId)
        };
        var failed = new FakeQueuedRequest(new ChangeValue(1), attempt: attempt, readException: exception,
            throwOnInvocation: throwInvocation);
        var next = new FakeQueuedRequest(new ChangeValue(9));
        var handler = new RecordingTerminalHandler();
        var logger = new CapturingLogger();
        using var deliveries = ListenToDeliveries(out var outcomes);
        using var faults = ListenToRunnerFaults(out var recordedFaults);
        var runner = new QueueRunner(new FakeQueueConsumer([failed, next]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = terminalAttempt },
            handler), logger);

        // Act
        await runner.RunAsync();

        // Assert
        Assert.Equal(terminal ? 1 : 0, handler.Failures.Count);
        Assert.False(failed.Completed);
        Assert.True(failed.Abandoned);
        Assert.False(failed.Completed && failed.Abandoned);
        Assert.Equal(0, failed.CompletionCount);
        Assert.Equal(1, failed.AbandonmentCount);
        Assert.True(next.Completed);
        Assert.Equal(2, outcomes.Count);
        Assert.Contains(terminal ? "terminal" : "abandoned", outcomes);
        Assert.Contains("completed", outcomes);
        var expectedFault = faultLogged || throwInvocation;
        Assert.Equal(expectedFault ? 1 : 0, recordedFaults.Count);
        Assert.Equal(expectedFault ? 1 : 0, logger.Entries.Count(entry => entry.EventId.Id == 1002));
        Assert.Equal(terminal ? 1 : 0, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
        Assert.Equal(terminal ? 1 : 0, logger.Entries.Count(entry => entry.EventId.Id == 1005));
        if (expectedFault)
        {
            var recorded = Assert.Single(logger.Entries, entry => entry.EventId.Id == 1002).Exception;
            if (throwInvocation)
                Assert.Equal("Invocation is unavailable.", recorded?.Message);
            else
                Assert.Same(exception, recorded);
        }
    }

    /// <summary>B11-at: An unclassified terminal read failure records its runner fault exactly once.</summary>
    [Fact]
    public Task ShouldRecordFaultGivenUnclassifiedReadFailureAtTerminalThreshold() =>
        ShouldApplyQueueReadFailurePolicyGivenMatrixCell("B11-at", "unclassified", 3, 3, true, true, false);

    /// <summary>D1: A throwing terminal handler faults once and retains ownership for representative terminal cells.</summary>
    [Theory]
    [InlineData("D1-B", "read")]
    [InlineData("D1-C2", "permanent")]
    [InlineData("D1-C5", "actor")]
    [InlineData("D1-C6", "transport")]
    public async Task ShouldRetainOwnershipGivenTerminalHandlerThrowsForMatrixCell(string cellId, string situation)
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var failed = situation switch
        {
            "read" => new FakeQueuedRequest(new ChangeValue(1),
                readException: RequestEnvelopeFailure.Create(RequestEnvelopeFailureKind.Permanent, cellId)),
            "permanent" => new FakeQueuedRequest(new InvalidChangeValue(1)),
            "actor" => new FakeQueuedRequest(new ChangeValue(1), actorToken: "expired-token"),
            _ => new FakeQueuedRequest(new ChangeValue(1), transportMismatch: true)
        };
        var next = new FakeQueuedRequest(new ChangeValue(9));
        var terminal = new RecordingTerminalHandler(true);
        using var deliveries = ListenToDeliveries(out var outcomes);
        using var faults = ListenToRunnerFaults(out var recordedFaults);

        // Act
        var runner = new QueueRunner(new FakeQueueConsumer([failed, next]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator("expired-token"),
            options: new QueueRunnerOptions { RetryDelay = TimeSpan.Zero }, terminalHandler: terminal));

        // Assert
        _ = await Assert.ThrowsAsync<TerminalHandlerFailureException>(() => runner.RunAsync());

        Assert.Single(terminal.Failures);
        Assert.False(failed.Completed);
        Assert.False(failed.Abandoned);
        Assert.Equal(0, failed.Retries);
        Assert.False(next.Completed);
        Assert.False(next.Abandoned);
        Assert.Equal(["fault"], outcomes);
        Assert.Empty(recordedFaults);
    }

    /// <summary>Provides one named row for each B-axis policy cell.</summary>
    public static IEnumerable<object?[]> QueueReadFailureCases()
    {
        foreach (var id in new[] { "B1", "B2", "B3", "B5", "B6", "B7", "B9", "B10" })
        {
            yield return [$"{id}-below", "permanent", 3U, 2U, true, false, false];
            yield return [$"{id}-at", "permanent", 3U, 3U, true, false, false];
            yield return [$"{id}-none", "permanent", null, 1U, true, false, false];
        }

        foreach (var id in new[] { "B4", "B8" })
        {
            yield return [$"{id}-below", "retryable", 3U, 2U, false, true, false];
            yield return [$"{id}-at", "retryable", 3U, 3U, true, false, false];
            yield return [$"{id}-none", "retryable", null, 1U, false, true, false];
        }

        yield return ["B11-below", "unclassified", 3U, 2U, false, true, false];
        yield return ["B11-none", "unclassified", null, 1U, false, true, false];
        yield return ["B12-below-invocation", "retryable", 3U, 2U, false, true, true];
        yield return ["B12-at-invocation", "retryable", 3U, 3U, true, false, true];
        yield return ["B12-none-invocation", "retryable", null, 1U, false, true, true];
    }

    static MeterListener ListenToDeliveries(out List<string> outcomes)
    {
        var captured = new List<string>();
        outcomes = captured;
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
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var values = tags.ToArray();
            lock (captured)
                captured.Add((string)values[2].Value!);
        });
        listener.Start();
        return listener;
    }

    static MeterListener ListenToRunnerFaults(out List<KeyValuePair<string, object?>[]> faults)
    {
        var captured = new List<KeyValuePair<string, object?>[]>();
        faults = captured;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == PortiaTelemetry.SourceName && instrument.Name == "portia.worker.failure")
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => captured.Add(tags.ToArray()));
        listener.Start();
        return listener;
    }

    sealed class FakeQueueConsumer(IReadOnlyList<IQueuedRequest> items) : IRequestQueueConsumer
    {
        public int EnumerationCount { get; private set; }

        public async IAsyncEnumerable<IQueuedRequest> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            EnumerationCount++;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return item;
            }
        }
    }

    sealed class OneThenWaitQueueConsumer(IQueuedRequest item) : IRequestQueueConsumer
    {
        readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Waiting => _waiting.Task;

        public async IAsyncEnumerable<IQueuedRequest> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return item;
            _waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    sealed class RecordingQueueScopeFactory(
        IRequestBus bus,
        IRequestActorValidator validator,
        Func<int, bool> includeHandler) : IQueueDeliveryScopeFactory
    {
        public List<RecordingQueueScope> Created { get; } = [];

        public ValueTask<IQueueDeliveryScope> CreateAsync(CancellationToken ct = default)
        {
            var scope = new RecordingQueueScope(bus, validator,
                includeHandler(Created.Count) ? new RecordingTerminalHandler() : null);
            Created.Add(scope);
            return ValueTask.FromResult<IQueueDeliveryScope>(scope);
        }
    }

    sealed class RecordingQueueScope(
        IRequestBus bus,
        IRequestActorValidator validator,
        IQueuedRequestTerminalHandler? terminalHandler) : IQueueDeliveryScope
    {
        public bool Disposed { get; private set; }
        public IRequestBus Bus { get; } = bus;
        public IRequestActorValidator ActorValidator { get; } = validator;
        public TimeProvider? TimeProvider => null;
        public QueueRunnerOptions Options { get; } = new();
        public IQueuedRequestTerminalHandler? TerminalHandler { get; } = terminalHandler;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    sealed class FakeQueuedRequest(
        IRequest request,
        bool throwOnDispatch = false,
        string? actorToken = "valid-token",
        uint attempt = 1,
        List<string>? operations = null,
        bool throwOnAcknowledge = false,
        bool supportsDurableAttempts = true,
        bool transportMismatch = false,
        bool invocationMayBeReadOnce = false,
        Exception? readException = null,
        bool throwOnInvocation = false,
        RequestTraceContext? traceContext = null,
        RequestMetadata? metadata = null,
        bool throwOnRetry = false,
        CancellationToken reservation = default) : IQueuedRequest
    {
        public CancellationToken ReservationCancellation => reservation;
        public RequestTraceContext? TraceContext => traceContext;

        public int CompletionCount { get; private set; }

        public int AbandonmentCount { get; private set; }
        public int Retries { get; private set; }
        public TimeSpan? RetryDelay { get; private set; }

        public bool Completed { get; private set; }

        public bool Abandoned { get; private set; }
        public int InvocationReadCount { get; private set; }
        public RequestMetadata Metadata { get; } = metadata ?? RequestMetadata.Create();

        public RequestInvocation Invocation => !throwOnInvocation &&
                                               (++InvocationReadCount == 1 || !invocationMayBeReadOnce)
            ? new QueueInvocation("queue://test/work/item", Attempt)
            : throw new InvalidOperationException("Invocation is unavailable.");

        public IRequest Request => readException is not null
            ? throw readException
            : transportMismatch
                ? throw new InvalidRequestTransportException(request, RequestTransportId.Queue)
                : throwOnDispatch
                    ? new ThrowingChangeValue(0)
                    : request;

        public string? ActorToken { get; } = actorToken;

        public uint Attempt => attempt;

        public bool SupportsDurableAttempts => supportsDurableAttempts;

        public ValueTask CompleteAsync(CancellationToken ct = default)
        {
            CompletionCount++;
            Completed = true;
            operations?.Add("complete");
            return throwOnAcknowledge
                ? ValueTask.FromException(new IOException("The broker refused the acknowledgment."))
                : ValueTask.CompletedTask;
        }

        public ValueTask AbandonAsync(CancellationToken ct = default)
        {
            AbandonmentCount++;
            Abandoned = true;
            operations?.Add("abandon");
            return ValueTask.CompletedTask;
        }

        public ValueTask RetryAsync(TimeSpan delay, CancellationToken ct = default)
        {
            Retries++;
            RetryDelay = delay;
            operations?.Add("retry");
            return throwOnRetry
                ? ValueTask.FromException(new IOException("The broker refused retry."))
                : ValueTask.CompletedTask;
        }
    }

    sealed class RecordingTerminalHandler(bool throws = false, List<string>? operations = null)
        : IQueuedRequestTerminalHandler
    {
        public List<QueuedRequestFailureContext> Failures { get; } = [];

        public ValueTask HandleAsync(QueuedRequestFailureContext context, CancellationToken ct = default)
        {
            Failures.Add(context);
            operations?.Add("terminal");
            return throws
                ? ValueTask.FromException(new InvalidOperationException("terminal failed"))
                : ValueTask.CompletedTask;
        }
    }

    sealed class LegacyQueuedRequest(IQueuedRequest inner) : IQueuedRequest
    {
        public IRequest Request => inner.Request;
        public RequestMetadata Metadata => inner.Metadata;
        public RequestInvocation Invocation => inner.Invocation;
        public uint Attempt => inner.Attempt;
        public string? ActorToken => inner.ActorToken;
        public CancellationToken ReservationCancellation => inner.ReservationCancellation;
        public ValueTask CompleteAsync(CancellationToken ct = default) => inner.CompleteAsync(ct);
        public ValueTask AbandonAsync(CancellationToken ct = default) => inner.AbandonAsync(ct);
    }

    sealed record ThrowingChangeValue(int Value) : IRequest;

    internal sealed record InvalidChangeValue(int Value, bool IsTransient = false) : IRequest;

    internal sealed class InvalidChangeValueHandler : IRequestHandler<InvalidChangeValue>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<InvalidChangeValue> context, CancellationToken ct) =>
            ValueTask.FromResult(Result.Failure(new RequestError(RequestErrorKind.Validation, "Value is invalid.",
                context.Request.IsTransient)));
    }

    // Runs a callback once the inner bus has returned, to change transport state between dispatch
    // and settlement.
    sealed class AfterDispatchBus(IRequestBus inner, Action dispatched) : IRequestBus
    {
        public RequestDispatchContext CreateContext(ClaimsPrincipal actor, RequestMetadata? metadata = null) =>
            inner.CreateContext(actor, metadata);

        public ValueTask<Result> AuthorizeAsync(IRequestBase request, RequestDispatchContext context,
            CancellationToken ct = default) => inner.AuthorizeAsync(request, context, ct);

        public async ValueTask<Result> DispatchAsync(IRequest request, RequestDispatchContext context,
            CancellationToken ct = default)
        {
            var result = await inner.DispatchAsync(request, context, ct);
            dispatched();
            return result;
        }

        public ValueTask<Result<TOut>> DispatchAsync<TOut>(IRequest<TOut> request, RequestDispatchContext context,
            CancellationToken ct = default) => inner.DispatchAsync(request, context, ct);

        public IAsyncEnumerable<TOut> DispatchStreamAsync<TOut>(IStreamRequest<TOut> request,
            RequestDispatchContext context, CancellationToken ct = default) =>
            inner.DispatchStreamAsync(request, context, ct);
    }

    sealed class CapturingLogger : ILogger<QueueRunner>
    {
        public List<(LogLevel Level, EventId EventId, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId, formatter(state, exception), exception));
    }
}
