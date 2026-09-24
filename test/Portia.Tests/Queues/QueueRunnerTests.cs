using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
///     Verifies that queued requests dispatch through the same request bus as any other origin.
/// </summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class QueueRunnerTests
{
    /// <summary>Each reservation records one final disposition and only terminal/fault logs.</summary>
    [Fact]
    public async Task ShouldRecordOneDeliveryOutcomeWithoutRetryOrBusinessErrorLogNoise()
    {
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

        await runner.RunAsync();

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
    public async Task ShouldDispatchAndCompleteEveryQueuedRequest()
    {
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

        await runner.RunAsync();

        Assert.Equal(3, handler.LastValue);
        Assert.All(items, item => Assert.True(item.Completed));
        Assert.All(items, item => Assert.False(item.Abandoned));
    }

    /// <summary>
    ///     Verifies that a failed dispatch abandons the request instead of stopping the run, and
    ///     still dispatches the requests that follow it.
    /// </summary>
    [Fact]
    public async Task ShouldAbandonFailedRequestAndContinue()
    {
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var bus = busHost.Bus;
        var failing = new FakeQueuedRequest(new ChangeValue(1), true);
        var succeeding = new FakeQueuedRequest(new ChangeValue(2));
        var consumer = new FakeQueueConsumer([failing, succeeding]);
        var runner = new QueueRunner(consumer, RequestDeliveryScopes.FixedQueue(bus, new TestRequestActorValidator(),
            terminalHandler: new RecordingTerminalHandler()));

        await runner.RunAsync();

        Assert.False(failing.Completed);
        Assert.True(failing.Abandoned);
        Assert.True(succeeding.Completed);
        Assert.Equal(2, handler.LastValue);
    }

    /// <summary>
    ///     Verifies that a non-transient <see cref="RequestError" /> completes (drops) the request
    ///     instead of abandoning it for redelivery, since it would fail identically again.
    /// </summary>
    [Fact]
    public async Task ShouldCompleteNonTransientFailureInsteadOfAbandoning()
    {
        using var busHost = TestRequestBus.Create();
        var bus = busHost.Bus;
        var invalid = new FakeQueuedRequest(new InvalidChangeValue(1));
        var terminal = new RecordingTerminalHandler();
        var consumer = new FakeQueueConsumer([invalid]);
        var runner = new QueueRunner(consumer,
            RequestDeliveryScopes.FixedQueue(bus, new TestRequestActorValidator(), terminalHandler: terminal));

        await runner.RunAsync();

        var failure = Assert.Single(terminal.Failures);
        Assert.Equal(QueuedRequestTerminalReason.PermanentFailure, failure.Reason);
        Assert.Same(invalid.Request, failure.Request);
        Assert.Same(invalid.Metadata, failure.Metadata);
        Assert.Equal(invalid.Invocation, failure.Invocation);
        Assert.Equal(invalid.Attempt, failure.Attempt);
        Assert.Equal("Value is invalid.", failure.Error?.Message);
        Assert.Null(failure.Exception);
        Assert.True(invalid.Completed);
        Assert.False(invalid.Abandoned);
    }

    /// <summary>A direct runner refuses to enumerate without an application terminal policy.</summary>
    [Fact]
    public async Task ShouldRejectMissingHandlerBeforeConsumerEnumeration()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new InvalidChangeValue(1));
        var consumer = new FakeQueueConsumer([queued]);
        var runner = new QueueRunner(consumer,
            RequestDeliveryScopes.FixedQueue(busHost.Bus, new TestRequestActorValidator()));

        var failure = await Assert.ThrowsAsync<QueueConfigurationException>(() => runner.RunAsync());

        Assert.Equal(typeof(IQueuedRequestTerminalHandler), failure.MissingServiceType);
        Assert.Contains(typeof(IQueuedRequestTerminalHandler).FullName!, failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, consumer.EnumerationCount);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>Even a queue containing only successful work requires an explicit terminal policy.</summary>
    [Fact]
    public async Task ShouldRejectMissingHandlerForSuccessfulOnlyQueue()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1));
        var runner = new QueueRunner(new FakeQueueConsumer([queued]),
            RequestDeliveryScopes.FixedQueue(busHost.Bus, new TestRequestActorValidator()));

        _ = await Assert.ThrowsAsync<QueueConfigurationException>(() => runner.RunAsync());

        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>
    ///     Verifies that a request whose carried actor token fails re-validation — e.g. it expired
    ///     since it was enqueued — is dropped (completed, not redelivered) without ever reaching the
    ///     handler. Retrying would not make an expired token valid, so this must never be abandoned
    ///     for redelivery, and expiry must always be honored even though the request already made it
    ///     onto the queue.
    /// </summary>
    [Fact]
    public async Task ShouldCompleteRequestWithoutDispatchingWhenActorTokenFailsRevalidation()
    {
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var bus = busHost.Bus;
        var expired = new FakeQueuedRequest(new ChangeValue(1), actorToken: "expired-token");
        var terminal = new RecordingTerminalHandler();
        var consumer = new FakeQueueConsumer([expired]);
        var runner = new QueueRunner(consumer,
            RequestDeliveryScopes.FixedQueue(bus, new TestRequestActorValidator("expired-token"),
                terminalHandler: terminal));

        await runner.RunAsync();

        var failure = Assert.Single(terminal.Failures);
        Assert.Equal(QueuedRequestTerminalReason.ActorValidationFailure, failure.Reason);
        Assert.Equal(RequestErrorKind.Unauthorized, failure.Error?.Kind);
        Assert.Null(failure.Exception);
        Assert.True(expired.Completed);
        Assert.False(expired.Abandoned);
        Assert.Null(handler.LastValue);
    }

    /// <summary>
    ///     Verifies that a request whose actor token re-validation fails does not stop the run — the
    ///     requests that follow it still dispatch normally.
    /// </summary>
    [Fact]
    public async Task ShouldContinueAfterActorTokenRevalidationFailure()
    {
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

        await runner.RunAsync();

        Assert.True(expired.Completed);
        Assert.True(valid.Completed);
        Assert.Equal(2, handler.LastValue);
    }

    /// <summary>Callback completion precedes the one transport acknowledgment.</summary>
    [Fact]
    public async Task ShouldCompleteCallbackBeforeAcknowledgingTerminalDeliveryExactlyOnce()
    {
        using var busHost = TestRequestBus.Create();
        var operations = new List<string>();
        var queued = new FakeQueuedRequest(new InvalidChangeValue(1), operations: operations);
        var terminal = new RecordingTerminalHandler(operations: operations);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), terminalHandler: terminal));

        await runner.RunAsync();

        Assert.Equal(["terminal", "complete"], operations);
        Assert.Equal(1, queued.CompletionCount);
        Assert.Equal(0, queued.AbandonmentCount);
    }

    /// <summary>The transport invocation is snapshot data even when terminal handling inspects it.</summary>
    [Fact]
    public async Task ShouldReadInvocationOnceThroughTerminalCallbackAndAcknowledgment()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new InvalidChangeValue(1), invocationMayBeReadOnce: true);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), terminalHandler: terminal));

        await runner.RunAsync();

        Assert.Equal(1, queued.InvocationReadCount);
        _ = Assert.Single(terminal.Failures);
        Assert.Equal(1, queued.CompletionCount);
    }

    /// <summary>
    ///     Verifies that a broker which refuses the acknowledgment after a terminal delivery has already
    ///     been handled does not fault the runner. The application's terminal handler has run and its
    ///     side effects are done; killing the run would only stop every later delivery over a failure
    ///     that redelivery already covers, so the fault is recorded and the run continues.
    /// </summary>
    [Fact]
    public async Task ShouldContinueAndRecordAFaultWhenTheBrokerRefusesATerminalAcknowledgment()
    {
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

        await runner.RunAsync();

        _ = Assert.Single(terminal.Failures);
        Assert.Equal(1, refused.CompletionCount);
        Assert.True(later.Completed);
        meterListener.Dispose();
        Assert.Contains(faults, tags => tags.Any(tag => Equals(tag.Value, nameof(QueueRunner)))
                                        && tags.Any(tag => Equals(tag.Value, "cleanup")));
        Assert.Equal(["completed", "fault"], deliveryOutcomes.Order());
    }

    /// <summary>
    ///     A request its handler completed is never dead-lettered, even at the terminal attempt: a refused
    ///     acknowledgment is a cleanup fault, and the transport's redelivery is the at-least-once path.
    /// </summary>
    [Fact]
    public async Task ShouldNotDeadLetterASuccessfulRequestWhoseAcknowledgmentFailsAtTerminalAttempt()
    {
        using var busHost = TestRequestBus.Create();
        using var deliveryMeter = ListenToDeliveries(out var deliveryOutcomes);
        var queued = new FakeQueuedRequest(new ChangeValue(5), attempt: 3, throwOnAcknowledge: true);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }, terminal));

        await runner.RunAsync();

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
    public async Task ShouldLeaveADeliveryWhoseReservationWasLostToTheTransport()
    {
        using var busHost = TestRequestBus.Create();
        using var deliveryMeter = ListenToDeliveries(out var deliveryOutcomes);
        using var lost = new CancellationTokenSource();
        await lost.CancelAsync();
        var queued = new FakeQueuedRequest(new ChangeValue(5), true, attempt: 3, reservation: lost.Token);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }, terminal));

        await runner.RunAsync();

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
    public async Task ShouldLeaveADeliveryWhoseReservationIsLostWhileItsHandlerRuns(bool fails)
    {
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

        await runner.RunAsync();

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
    public async Task ShouldTreatHostShutdownAfterDispatchAsCancellationNotALostReservation()
    {
        using var busHost = TestRequestBus.Create();
        using var deliveryMeter = ListenToDeliveries(out var deliveryOutcomes);
        using var host = new CancellationTokenSource();
        using var reservation = CancellationTokenSource.CreateLinkedTokenSource(host.Token);
        var logger = new CapturingLogger();
        var queued = new FakeQueuedRequest(new ChangeValue(5), reservation: reservation.Token);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            new AfterDispatchBus(busHost.Bus, host.Cancel), new TestRequestActorValidator(),
            terminalHandler: new RecordingTerminalHandler()), logger);

        await runner.RunAsync(host.Token);

        Assert.True(queued.Completed);
        Assert.Empty(logger.Entries);
        Assert.Equal(["completed"], deliveryOutcomes);
    }

    /// <summary>A retryable handler result at the configured threshold becomes terminal.</summary>
    [Fact]
    public async Task ShouldInvokeTerminalHandlerGivenRetryableResultAtTerminalAttempt()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new InvalidChangeValue(1, true), attempt: 3);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }, terminal));

        await runner.RunAsync();

        var failure = Assert.Single(terminal.Failures);
        Assert.Equal(QueuedRequestTerminalReason.RetryLimitReached, failure.Reason);
        Assert.True(failure.Error?.IsTransient);
        Assert.Null(failure.Exception);
        Assert.True(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>An unknown wire contract remains transport-owned below its durable threshold.</summary>
    [Fact]
    public async Task ShouldAbandonRetryableEnvelopeFailureBelowTerminalAttempt()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), attempt: 2,
            readException: RequestEnvelopeFailure.Create(RequestEnvelopeFailureKind.Retryable, "unknown contract"));
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }, terminal));

        await runner.RunAsync();

        Assert.Empty(terminal.Failures);
        Assert.True(queued.Abandoned);
        Assert.False(queued.Completed);
    }

    /// <summary>A retryable read failure becomes terminal only when both threshold and callback exist.</summary>
    [Fact]
    public async Task ShouldCompleteRetryableEnvelopeFailureAtTerminalAttemptGivenHandler()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), attempt: 3,
            readException: RequestEnvelopeFailure.Create(RequestEnvelopeFailureKind.Retryable, "unknown contract"));
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }, terminal));

        await runner.RunAsync();

        Assert.Equal(QueuedRequestTerminalReason.RetryLimitReached, Assert.Single(terminal.Failures).Reason);
        Assert.True(queued.Completed);
    }

    /// <summary>A permanent lazy read always reaches the terminal callback and later work continues.</summary>
    [Fact]
    public async Task ShouldTerminalizePermanentEnvelopeFailureAndContinue()
    {
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var malformed = new FakeQueuedRequest(new ChangeValue(1),
            readException: RequestEnvelopeFailure.Create(RequestEnvelopeFailureKind.Permanent, "malformed"));
        var later = new FakeQueuedRequest(new ChangeValue(7));
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([malformed, later]),
            RequestDeliveryScopes.FixedQueue(busHost.Bus, new TestRequestActorValidator(), terminalHandler: terminal));

        await runner.RunAsync();

        Assert.Equal(QueuedRequestTerminalReason.DeserializationFailure, Assert.Single(terminal.Failures).Reason);
        Assert.True(malformed.Completed);
        Assert.False(malformed.Abandoned);
        Assert.True(later.Completed);
        Assert.Equal(7, handler.LastValue);
    }

    /// <summary>An abandoned lazy read failure is visible exactly once as an execution fault.</summary>
    [Fact]
    public async Task ShouldRecordAbandonedReadFailureExactlyOnce()
    {
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

        await runner.RunAsync();

        Assert.True(queued.Abandoned);
        var fault = Assert.Single(faults);
        Assert.Contains(fault, tag => Equals(tag.Value, nameof(QueueRunner)));
        Assert.Contains(fault, tag => Equals(tag.Value, "execution"));
    }

    /// <summary>A malformed read with an application callback retains deserialization terminal semantics.</summary>
    [Fact]
    public async Task ShouldCompletePermanentEnvelopeFailureAsDeserializationFailureGivenHandler()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1),
            readException: RequestEnvelopeFailure.Create(RequestEnvelopeFailureKind.Permanent, "malformed"));
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), terminalHandler: terminal));

        await runner.RunAsync();

        Assert.Equal(QueuedRequestTerminalReason.DeserializationFailure, Assert.Single(terminal.Failures).Reason);
        Assert.True(queued.Completed);
    }

    /// <summary>A retryable lazy read is terminalized at its durable threshold.</summary>
    [Fact]
    public async Task ShouldTerminalizeRetryableEnvelopeFailureAtThreshold()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), attempt: 3,
            readException: RequestEnvelopeFailure.Create(RequestEnvelopeFailureKind.Retryable, "unknown contract"));
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }, terminal));

        await runner.RunAsync();

        Assert.Equal(QueuedRequestTerminalReason.RetryLimitReached, Assert.Single(terminal.Failures).Reason);
        Assert.False(queued.Abandoned);
        Assert.True(queued.Completed);
    }

    /// <summary>A terminal threshold is rejected by the runner when the adapter has no durable count.</summary>
    [Fact]
    public async Task ShouldRejectTerminalAttemptGivenTransportDoesNotSupportDurableAttempts()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), supportsDurableAttempts: false);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 },
            new RecordingTerminalHandler()));

        var error = await Assert.ThrowsAsync<QueueConfigurationException>(() => runner.RunAsync());

        Assert.Contains("durable delivery-attempt count", error.Message, StringComparison.Ordinal);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>Zero is not a meaningful terminal delivery attempt.</summary>
    [Fact]
    public async Task ShouldRejectZeroTerminalAttemptBeforeReadingTheDelivery()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1));
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 0 },
            new RecordingTerminalHandler()));

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
        using var busHost = TestRequestBus.Create();
        var consumer = new FakeQueueConsumer([
            new FakeQueuedRequest(new ChangeValue(1), supportsDurableAttempts: durable),
            new FakeQueuedRequest(new ChangeValue(2), supportsDurableAttempts: durable)
        ]);
        var runner = new QueueRunner(consumer, RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = terminalAttempt },
            new RecordingTerminalHandler()));
        using var hosted = new QueueRunnerHostedService(runner);

        await hosted.StartAsync(default);
        _ = await Assert.ThrowsAsync<QueueConfigurationException>(async () =>
            await (hosted.ExecuteTask ?? throw new InvalidOperationException("The hosted runner did not start."))
                .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>A request that did not declare queue delivery is terminal and never dispatched.</summary>
    [Fact]
    public async Task ShouldCompleteAsInvalidTransportGivenQueueCapabilityMismatch()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), transportMismatch: true);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), terminalHandler: terminal));

        await runner.RunAsync();

        var failure = Assert.Single(terminal.Failures);
        Assert.Equal(QueuedRequestTerminalReason.InvalidTransport, failure.Reason);
        _ = Assert.IsType<InvalidRequestTransportException>(failure.Exception);
        Assert.True(queued.Completed);
    }

    /// <summary>An unexpected exception at the configured threshold also becomes terminal.</summary>
    [Fact]
    public async Task ShouldInvokeTerminalHandlerGivenUnexpectedFailureAtTerminalAttempt()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), true, attempt: 3);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }, terminal));

        await runner.RunAsync();

        var failure = Assert.Single(terminal.Failures);
        Assert.Equal((uint)3, failure.Attempt);
        Assert.Equal(QueuedRequestTerminalReason.RetryLimitReached, failure.Reason);
        Assert.NotNull(failure.Exception);
        Assert.True(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>A retryable result remains broker-owned until a configured threshold is reached.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(3U)]
    public async Task ShouldAbandonRetryableResultBelowThresholdOrWithoutThreshold(uint? terminalAttempt)
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new InvalidChangeValue(1, true), attempt: 2);
        var terminal = new RecordingTerminalHandler();
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = terminalAttempt },
            terminal));

        await runner.RunAsync();

        Assert.Empty(terminal.Failures);
        Assert.False(queued.Completed);
        Assert.True(queued.Abandoned);
    }

    /// <summary>A terminal callback failure leaves ownership with the transport.</summary>
    [Fact]
    public async Task ShouldLeaveDeliveryUnacknowledgedGivenTerminalHandlerFailureWhenAttemptIsTerminal()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), true, attempt: 3);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 },
            new RecordingTerminalHandler(true)));

        var failure = await Assert.ThrowsAsync<TerminalHandlerFailureException>(() => runner.RunAsync());

        Assert.Equal("terminal failed", failure.InnerException?.Message);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>The hosted transport must not turn a terminal callback fault into a restart loop.</summary>
    [Fact]
    public async Task ShouldFaultHostedRunnerGivenTerminalHandlerFailure()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), true, attempt: 3);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 },
            new RecordingTerminalHandler(true)));
        using var hosted = new QueueRunnerHostedService(runner);

        await hosted.StartAsync(default);
        var failure = await Assert.ThrowsAsync<TerminalHandlerFailureException>(async () =>
            await (hosted.ExecuteTask ?? throw new InvalidOperationException("The hosted runner did not start.")));

        Assert.Equal("terminal failed", failure.InnerException?.Message);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>A configured retry threshold cannot make a missing terminal policy valid.</summary>
    [Fact]
    public async Task ShouldFaultWithoutAcknowledgmentGivenRetryLimitAndMissingHandler()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), true, attempt: 3);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 }));

        var failure = await Assert.ThrowsAsync<QueueConfigurationException>(() => runner.RunAsync());

        Assert.Equal(typeof(IQueuedRequestTerminalHandler), failure.MissingServiceType);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>Failures remain retryable below the configured terminal threshold.</summary>
    [Fact]
    public async Task ShouldAbandonGivenFailureBelowRetryLimitAndMissingHandler()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new ChangeValue(1), true, attempt: 2);
        var runner = new QueueRunner(new FakeQueueConsumer([queued]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = 3 },
            new RecordingTerminalHandler()));

        await runner.RunAsync();

        Assert.False(queued.Completed);
        Assert.True(queued.Abandoned);
    }

    /// <summary>The hosted transport exposes startup configuration failure instead of restarting.</summary>
    [Fact]
    public async Task ShouldFaultHostedRunnerGivenTerminalHandlerMissing()
    {
        using var busHost = TestRequestBus.Create();
        var queued = new FakeQueuedRequest(new InvalidChangeValue(1));
        var runner = new QueueRunner(new FakeQueueConsumer([queued]),
            RequestDeliveryScopes.FixedQueue(busHost.Bus, new TestRequestActorValidator()));
        using var hosted = new QueueRunnerHostedService(runner);

        await hosted.StartAsync(default);
        var failure = await Assert.ThrowsAsync<QueueConfigurationException>(async () =>
            await (hosted.ExecuteTask ?? throw new InvalidOperationException("The hosted runner did not start.")));

        Assert.Equal(typeof(IQueuedRequestTerminalHandler), failure.MissingServiceType);
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>Fitz's combined worker host must propagate both terminal ownership faults.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldFaultFitzWorkerRestartBoundaryGivenTerminalOwnershipFailure(bool missing)
    {
        var expected = missing
            ? (Exception)new TerminalHandlerMissingException(QueuedRequestTerminalReason.PermanentFailure)
            : new TerminalHandlerFailureException(new InvalidOperationException("terminal failed"));
        var attempts = 0;

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
        var expected = new QueueConfigurationException(typeof(IQueuedRequestTerminalHandler));
        var attempts = 0;

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
    public async Task ShouldDisposePreflightScopeAndUseFreshDeliveryScope()
    {
        using var busHost = TestRequestBus.Create();
        var scopes = new RecordingQueueScopeFactory(busHost.Bus, new TestRequestActorValidator(),
            _ => true);
        var queued = new FakeQueuedRequest(new ChangeValue(1));

        await new QueueRunner(new FakeQueueConsumer([queued]), scopes).RunAsync();

        Assert.Equal(2, scopes.Created.Count);
        Assert.All(scopes.Created, scope => Assert.True(scope.Disposed));
        Assert.NotSame(scopes.Created[0].TerminalHandler, scopes.Created[1].TerminalHandler);
        Assert.True(queued.Completed);
    }

    /// <summary>Every delivery scope is checked even after startup preflight succeeds.</summary>
    [Fact]
    public async Task ShouldRejectInconsistentDeliveryScopeWithoutChangingTransportOwnership()
    {
        using var busHost = TestRequestBus.Create();
        var scopes = new RecordingQueueScopeFactory(busHost.Bus, new TestRequestActorValidator(),
            index => index == 0);
        var queued = new FakeQueuedRequest(new ChangeValue(1));

        _ = await Assert.ThrowsAsync<QueueConfigurationException>(() =>
            new QueueRunner(new FakeQueueConsumer([queued]), scopes).RunAsync());

        Assert.Equal(2, scopes.Created.Count);
        Assert.All(scopes.Created, scope => Assert.True(scope.Disposed));
        Assert.False(queued.Completed);
        Assert.False(queued.Abandoned);
    }

    /// <summary>B1-B12: Queue read failures preserve disposition, continuation, telemetry, and ownership invariants.</summary>
    [Theory]
    [MemberData(nameof(QueueReadFailureCases))]
    public async Task ShouldApplyQueueReadFailurePolicyGivenMatrixCell(string cellId, string failureKind,
        uint? terminalAttempt, uint attempt, bool terminal, bool faultLogged, bool throwInvocation)
    {
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
        using var deliveries = ListenToDeliveries(out var outcomes);
        using var faults = ListenToRunnerFaults(out var recordedFaults);
        var runner = new QueueRunner(new FakeQueueConsumer([failed, next]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator(), new QueueRunnerOptions { TerminalAttempt = terminalAttempt },
            handler));

        await runner.RunAsync();

        Assert.Equal(terminal ? 1 : 0, handler.Failures.Count);
        Assert.Equal(terminal, failed.Completed);
        Assert.Equal(!terminal, failed.Abandoned);
        Assert.False(failed.Completed && failed.Abandoned);
        Assert.Equal(terminal ? 1 : 0, failed.CompletionCount);
        Assert.Equal(terminal ? 0 : 1, failed.AbandonmentCount);
        Assert.True(next.Completed);
        Assert.Equal(2, outcomes.Count);
        Assert.Contains(terminal ? "terminal" : "abandoned", outcomes);
        Assert.Contains("completed", outcomes);
        Assert.Equal(faultLogged ? 1 : 0, recordedFaults.Count);
    }

    /// <summary>B11-at: An unclassified terminal read failure records its runner fault exactly once.</summary>
    [Fact(Skip = "POLICY CONFLICT: B11-at")]
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
        var runner = new QueueRunner(new FakeQueueConsumer([failed, next]), RequestDeliveryScopes.FixedQueue(
            busHost.Bus, new TestRequestActorValidator("expired-token"), terminalHandler: terminal));

        _ = await Assert.ThrowsAsync<TerminalHandlerFailureException>(() => runner.RunAsync());

        Assert.Single(terminal.Failures);
        Assert.False(failed.Completed);
        Assert.False(failed.Abandoned);
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
        CancellationToken reservation = default) : IQueuedRequest
    {
        public CancellationToken ReservationCancellation => reservation;

        public int CompletionCount { get; private set; }

        public int AbandonmentCount { get; private set; }

        public bool Completed { get; private set; }

        public bool Abandoned { get; private set; }
        public int InvocationReadCount { get; private set; }
        public RequestMetadata Metadata { get; } = RequestMetadata.Create();

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
