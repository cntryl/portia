using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
///     Consumes requests off a queue and dispatches each to the request bus, so a command's handler
///     is the same regardless of whether it originated over HTTP, a queue, or RPC.
/// </summary>
/// <param name="consumer">The long-lived transport consumer.</param>
/// <param name="scopeFactory">Creates each delivery's application dependencies and terminal policy.</param>
/// <param name="logger">Reports failed deliveries.</param>
public sealed class QueueRunner(
    IRequestQueueConsumer consumer,
    IQueueDeliveryScopeFactory scopeFactory,
    ILogger<QueueRunner>? logger = null)
{
    readonly IRequestQueueConsumer _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
    readonly ILogger<QueueRunner>? _logger = logger;

    readonly IQueueDeliveryScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    /// <summary>
    ///     Reserves and dispatches queued requests until the queue is exhausted or cancellation is
    ///     requested. A request is completed after its handler succeeds. Permanent handler failures,
    ///     actor-validation failures, and retryable or unexpected failures at the configured
    ///     terminal attempt are first passed to the application terminal handler and are completed
    ///     only after that callback succeeds. A terminal delivery without a handler faults the
    ///     runner and remains transport-owned. Retryable or unexpected failures below the threshold
    ///     are abandoned for redelivery.
    /// </summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the run.</returns>
    public async Task RunAsync(CancellationToken ct = default)
    {
        await using (var startupScope = await _scopeFactory.CreateAsync(ct).ConfigureAwait(false))
        {
            ValidateScope(startupScope);
        }

        await foreach (var queued in _consumer.ReadAsync(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            var requestName = "unknown";
            var transport = "queue";
            var deliveryOutcome = RequestDeliveryOutcome.Fault;
            try
            {
                await using var scope = await _scopeFactory.CreateAsync(ct).ConfigureAwait(false);
                ValidateScope(scope);
                if (scope.Options.TerminalAttempt is > 0 && !queued.SupportsDurableAttempts)
                {
                    throw new InvalidOperationException(
                        "QueueRunnerOptions.TerminalAttempt requires a transport with a durable delivery-attempt count.");
                }

                try
                {
                    RequestInvocation invocation;
                    IRequest request;
                    RequestMetadata metadata;
                    RequestTraceContext? traceContext;
                    string? actorToken;
                    string? wireName;
                    try
                    {
                        invocation = queued.Invocation;
                        transport = invocation.TransportName;
                        wireName = queued.Name;
                        requestName = wireName ?? "unknown";
                        request = queued.Request;
                        metadata = queued.Metadata;
                        traceContext = queued.TraceContext;
                        actorToken = queued.ActorToken;
                    }
                    catch (InvalidRequestTransportException mismatch)
                    {
                        var safeInvocation = TryReadInvocation(queued);
                        transport = safeInvocation.TransportName;
                        RequestMetadata? safeMetadata = null;
                        try
                        {
                            safeMetadata = queued.Metadata;
                        }
                        catch
                        {
                            // The transport mismatch remains the primary terminal reason.
                        }

                        deliveryOutcome = await CompleteTerminalAsync(scope, queued, null, mismatch,
                                QueuedRequestTerminalReason.InvalidTransport, requestName, ct, mismatch.Request,
                                safeMetadata, safeInvocation, false)
                            .ConfigureAwait(false);
                        continue;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        var safeInvocation = TryReadInvocation(queued);
                        transport = safeInvocation.TransportName;
                        deliveryOutcome = await HandleReadFailureAsync(scope, queued, ex, requestName, safeInvocation,
                                ct)
                            .ConfigureAwait(false);
                        continue;
                    }

                    using var delivery =
                        CancellationTokenSource.CreateLinkedTokenSource(ct, queued.ReservationCancellation);
                    var dispatch = await RequestDispatch.SendAsync(scope.ActorValidator, scope.Bus, request,
                            RequestDelivery.For(request, wireName, invocation, metadata,
                                actorToken, traceContext, scope.TimeProvider), delivery.Token)
                        .ConfigureAwait(false);

                    // A lost reservation returned the delivery to the transport, which redelivers it: this
                    // runner no longer owns it and must not dead-letter, abandon, or acknowledge it.
                    if (queued.ReservationCancellation.IsCancellationRequested)
                    {
                        RecordLostReservation();
                        continue;
                    }

                    if (!dispatch.WasDispatched)
                    {
                        deliveryOutcome = await CompleteTerminalAsync(scope, queued, dispatch.Outcome.Error, null,
                                QueuedRequestTerminalReason.ActorValidationFailure, requestName, ct, request, metadata,
                                invocation)
                            .ConfigureAwait(false);
                    }
                    else if (dispatch.Outcome.IsSuccess)
                    {
                        // The request was handled, so it is never dead-lettered: a refused acknowledgment
                        // leaves the delivery with the transport, whose redelivery is the at-least-once path.
                        try
                        {
                            await queued.CompleteAsync(ct).ConfigureAwait(false);
                            deliveryOutcome = RequestDeliveryOutcome.Completed;
                        }
                        catch (Exception acknowledgmentException) when (!ct.IsCancellationRequested)
                        {
                            PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), RunnerFaultStage.Cleanup,
                                acknowledgmentException, _logger);
                        }
                    }
                    else if (dispatch.Outcome.Error is { IsTransient: false } permanent)
                    {
                        deliveryOutcome = await CompleteTerminalAsync(scope, queued, permanent, null,
                                QueuedRequestTerminalReason.PermanentFailure, requestName, ct, request, metadata,
                                invocation)
                            .ConfigureAwait(false);
                    }
                    else if (IsRetryLimitReached(scope, queued))
                    {
                        deliveryOutcome = await CompleteTerminalAsync(scope, queued, dispatch.Outcome.Error, null,
                                QueuedRequestTerminalReason.RetryLimitReached, requestName, ct, request, metadata,
                                invocation)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await queued.AbandonAsync(ct).ConfigureAwait(false);
                        deliveryOutcome = RequestDeliveryOutcome.Abandoned;
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested &&
                                           ex is not (TerminalHandlerFailureException or
                                               TerminalHandlerMissingException))
                {
                    if (ex is InvalidRequestTransportException mismatch)
                    {
                        deliveryOutcome = await CompleteTerminalAsync(scope, queued, null, mismatch,
                                QueuedRequestTerminalReason.InvalidTransport, requestName, ct, mismatch.Request)
                            .ConfigureAwait(false);
                        continue;
                    }

                    // An unrecognized exception's retriability is unknown; abandoning (rather than
                    // silently dropping the request) is the safer default.
                    PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), RunnerFaultStage.Execution, ex, _logger);
                    if (queued.ReservationCancellation.IsCancellationRequested)
                        continue;

                    if (IsRetryLimitReached(scope, queued))
                    {
                        deliveryOutcome = await CompleteTerminalAsync(scope, queued, null, ex,
                                QueuedRequestTerminalReason.RetryLimitReached, requestName, ct)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await queued.AbandonAsync(ct).ConfigureAwait(false);
                        deliveryOutcome = RequestDeliveryOutcome.Abandoned;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                deliveryOutcome = RequestDeliveryOutcome.Canceled;
                throw;
            }
            finally
            {
                PortiaTelemetry.RecordDelivery(requestName, transport, deliveryOutcome);
            }
        }
    }

    void RecordLostReservation() =>
        PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), RunnerFaultStage.Execution,
            new QueueReservationLostException(), _logger);

    static bool IsRetryLimitReached(IQueueDeliveryScope scope, IQueuedRequest queued) =>
        scope.Options.TerminalAttempt is { } terminal && queued.Attempt >= terminal;

    static void ValidateScope(IQueueDeliveryScope scope)
    {
        scope.Options.Validate();
        if (scope.TerminalHandler is null)
            throw new QueueConfigurationException(typeof(IQueuedRequestTerminalHandler));
    }

    static RequestInvocation TryReadInvocation(IQueuedRequest queued)
    {
        try
        {
            return queued.Invocation;
        }
        catch
        {
            return new QueueInvocation("unknown", 0);
        }
    }

    async ValueTask<RequestDeliveryOutcome> HandleReadFailureAsync(IQueueDeliveryScope scope,
        IQueuedRequest queued, Exception exception, string requestName, RequestInvocation invocation,
        CancellationToken ct)
    {
        var permanent = exception is JsonException ||
                        RequestEnvelopeFailure.GetKind(exception) is RequestEnvelopeFailureKind.Permanent;
        var terminal = permanent
            ? QueuedRequestTerminalReason.DeserializationFailure
            : QueuedRequestTerminalReason.RetryLimitReached;
        if (permanent || IsRetryLimitReached(scope, queued))
        {
            return await CompleteTerminalAsync(scope, queued, null, exception, terminal, requestName, ct,
                    invocation: invocation, readQueuedFields: false)
                .ConfigureAwait(false);
        }

        PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), RunnerFaultStage.Execution, exception, _logger);
        await queued.AbandonAsync(ct).ConfigureAwait(false);
        return RequestDeliveryOutcome.Abandoned;
    }

    async ValueTask<RequestDeliveryOutcome> CompleteTerminalAsync(IQueueDeliveryScope scope, IQueuedRequest queued,
        RequestError? error, Exception? exception, QueuedRequestTerminalReason reason, string requestName,
        CancellationToken ct, IRequest? request = null, RequestMetadata? metadata = null,
        RequestInvocation? invocation = null, bool readQueuedFields = true)
    {
        var terminalHandler = scope.TerminalHandler ?? throw new TerminalHandlerMissingException(reason);

        try
        {
            await terminalHandler.HandleAsync(new QueuedRequestFailureContext(
                        request ?? (readQueuedFields ? queued.Request : null),
                        metadata ?? (readQueuedFields ? queued.Metadata : null),
                        invocation ?? TryReadInvocation(queued), queued.Attempt, error, exception)
            { Reason = reason }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception handlerException) when (!ct.IsCancellationRequested)
        {
            throw new TerminalHandlerFailureException(handlerException);
        }

        try
        {
            await queued.CompleteAsync(ct).ConfigureAwait(false);
        }
        catch (Exception acknowledgmentException) when (!ct.IsCancellationRequested)
        {
            PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), RunnerFaultStage.Cleanup, acknowledgmentException,
                _logger);
            return RequestDeliveryOutcome.Fault;
        }

        PortiaTelemetry.RecordTerminalDelivery(requestName,
            (invocation ?? TryReadInvocation(queued)).TransportName, _logger);
        return RequestDeliveryOutcome.Terminal;
    }
}
