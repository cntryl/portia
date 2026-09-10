using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
/// Consumes requests off a queue and dispatches each to the request bus, so a command's handler
/// is the same regardless of whether it originated over HTTP, a queue, or RPC.
/// </summary>
/// <param name="consumer">The long-lived transport consumer.</param>
/// <param name="scopeFactory">Creates each delivery's application dependencies and terminal policy.</param>
/// <param name="logger">Reports failed deliveries.</param>
public sealed class QueueRunner(IRequestQueueConsumer consumer, IQueueDeliveryScopeFactory scopeFactory,
    ILogger<QueueRunner>? logger = null)
{
    readonly IRequestQueueConsumer _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
    readonly IQueueDeliveryScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    readonly ILogger<QueueRunner>? _logger = logger;

    /// <summary>
    /// Reserves and dispatches queued requests until the queue is exhausted or cancellation is
    /// requested. A request is completed after its handler succeeds, or after it fails with a
    /// non-transient error (bad input, unauthorized, a conflict — the request would fail
    /// identically on redelivery, so it is dropped, not retried). That includes actor
    /// re-validation failing (e.g. an expired token) — retrying won't make an expired token
    /// valid, so the request is dropped rather than redelivered forever. Any other failure — a
    /// transient error, or an unrecognized (unexpected, infrastructure-level) exception —
    /// abandons the reservation so the request is redelivered.
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
                using var delivery = CancellationTokenSource.CreateLinkedTokenSource(ct, queued.ReservationCancellation);
                var dispatch = await RequestDispatch.SendAsync(scope.ActorValidator, scope.Bus, queued.Request,
                    RequestDelivery.For(queued.Request, queued.Name, queued.Invocation, queued.Metadata,
                        queued.ActorToken, queued.TraceContext, scope.TimeProvider), delivery.Token).ConfigureAwait(false);

                if (!dispatch.WasDispatched)
                {
                    // Actor validation failed (e.g. an expired token) — retrying won't make it
                    // valid, so the request is dropped rather than redelivered forever.
                    PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), RunnerFaultStage.Validation, logger: _logger);
                    await queued.CompleteAsync(ct).ConfigureAwait(false);
                }
                else if (dispatch.Outcome.IsSuccess || dispatch.Outcome.Error is { IsTransient: false })
                {
                    await queued.CompleteAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    if (!await CompleteTerminalAsync(scope, queued, dispatch.Outcome.Error, null, ct).ConfigureAwait(false))
                        await queued.AbandonAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not TerminalHandlerFailureException)
            {
                // An unrecognized exception's retriability is unknown; abandoning (rather than
                // silently dropping the request) is the safer default.
                PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), RunnerFaultStage.Execution, ex, _logger);
                if (!await CompleteTerminalAsync(scope, queued, null, ex, ct).ConfigureAwait(false))
                    await queued.AbandonAsync(ct).ConfigureAwait(false);
            }
        }
    }

    async ValueTask<bool> CompleteTerminalAsync(IQueueDeliveryScope scope, IQueuedRequest queued, RequestError? error, Exception? exception, CancellationToken ct)
    {
        if (scope.Options.TerminalAttempt is not { } terminal || queued.Attempt < terminal || scope.TerminalHandler is null)
            return false;
        try
        {
            await scope.TerminalHandler.HandleAsync(new QueuedRequestFailureContext(
                queued.Request, queued.Metadata, queued.Invocation, queued.Attempt, error, exception), ct).ConfigureAwait(false);
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
            PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), RunnerFaultStage.Cleanup, acknowledgmentException, _logger);
        }
        return true;
    }
}
