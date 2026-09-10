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
        await foreach (var queued in _consumer.ReadAsync(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            await using var scope = await _scopeFactory.CreateAsync(ct).ConfigureAwait(false);
            try
            {
                using var delivery =
                    CancellationTokenSource.CreateLinkedTokenSource(ct, queued.ReservationCancellation);
                var dispatch = await RequestDispatch.SendAsync(scope.ActorValidator, scope.Bus, queued.Request,
                        RequestDelivery.For(queued.Request, queued.Name, queued.Invocation, queued.Metadata,
                            queued.ActorToken, queued.TraceContext, scope.TimeProvider), delivery.Token)
                    .ConfigureAwait(false);

                if (!dispatch.WasDispatched)
                {
                    PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), RunnerFaultStage.Validation,
                        logger: _logger);
                    await CompleteTerminalAsync(scope, queued, dispatch.Outcome.Error, null,
                            QueuedRequestTerminalReason.ActorValidationFailure, ct)
                        .ConfigureAwait(false);
                }
                else if (dispatch.Outcome.IsSuccess)
                {
                    await queued.CompleteAsync(ct).ConfigureAwait(false);
                }
                else if (dispatch.Outcome.Error is { IsTransient: false } permanent)
                {
                    await CompleteTerminalAsync(scope, queued, permanent, null,
                            QueuedRequestTerminalReason.PermanentFailure, ct)
                        .ConfigureAwait(false);
                }
                else if (IsRetryLimitReached(scope, queued))
                {
                    await CompleteTerminalAsync(scope, queued, dispatch.Outcome.Error, null,
                            QueuedRequestTerminalReason.RetryLimitReached, ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    await queued.AbandonAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                                       ex is not (TerminalHandlerFailureException or TerminalHandlerMissingException))
            {
                // An unrecognized exception's retriability is unknown; abandoning (rather than
                // silently dropping the request) is the safer default.
                PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), RunnerFaultStage.Execution, ex, _logger);
                if (IsRetryLimitReached(scope, queued))
                {
                    await CompleteTerminalAsync(scope, queued, null, ex,
                            QueuedRequestTerminalReason.RetryLimitReached, ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    await queued.AbandonAsync(ct).ConfigureAwait(false);
                }
            }
        }
    }

    static bool IsRetryLimitReached(IQueueDeliveryScope scope, IQueuedRequest queued) =>
        scope.Options.TerminalAttempt is { } terminal && queued.Attempt >= terminal;

    async ValueTask CompleteTerminalAsync(IQueueDeliveryScope scope, IQueuedRequest queued, RequestError? error,
        Exception? exception, QueuedRequestTerminalReason reason, CancellationToken ct)
    {
        var terminalHandler = scope.TerminalHandler ?? throw new TerminalHandlerMissingException(reason);

        try
        {
            await terminalHandler.HandleAsync(new QueuedRequestFailureContext(
                    queued.Request, queued.Metadata, queued.Invocation, queued.Attempt, error, exception)
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
        }

    }
}
