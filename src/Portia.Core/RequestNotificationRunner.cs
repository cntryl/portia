using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
/// Dispatches one-way request notifications (Fitz notice fanout, a fired Fitz schedule entry) to
/// the request bus, so a command's handler is the same regardless of origin. There is no queueing
/// or redelivery for this transport shape — a failed dispatch is simply lost, not retried.
/// </summary>
/// <param name="consumer">The long-lived notification consumer.</param>
/// <param name="scopeFactory">Creates each delivery's application dependencies.</param>
/// <param name="logger">Reports failed deliveries.</param>
public sealed class RequestNotificationRunner(IRequestNotificationConsumer consumer, IRequestDeliveryScopeFactory scopeFactory,
    ILogger<RequestNotificationRunner>? logger = null)
{
    readonly IRequestNotificationConsumer _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
    readonly IRequestDeliveryScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    readonly ILogger<RequestNotificationRunner>? _logger = logger;

    /// <summary>
    /// Reads and dispatches requests as they are delivered, until cancellation is requested.
    /// A failed dispatch does not stop the run; the request is simply lost, matching this
    /// transport's no-redelivery guarantee. Carried tokens are revalidated; scheduled requests
    /// instead arrive with an explicitly persisted system identity.
    /// </summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the run.</returns>
    public async Task RunAsync(CancellationToken ct = default)
    {
        await foreach (var delivered in _consumer.ReadAsync(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            try
            {
                await using var scope = await _scopeFactory.CreateAsync(ct).ConfigureAwait(false);
                // The result's failure category can't change anything at this transport's level
                // (no ack/redelivery). An unrecognized exception is still caught below.
                if (delivered.Actor is { } actor)
                {
                    if (delivered.Invocation is not ScheduleInvocation || !RequestActor.IsSystem(actor))
                        throw new InvalidOperationException("Only fired schedules may supply a trusted system actor.");
                    using var process = PortiaTelemetry.StartProcess(delivered.Request.GetType().Name, "fitz.schedule",
                        delivered.TraceContext, linked: true);
                    _ = await scope.Bus.DispatchAsync(delivered.Request,
                        new RequestDispatchContext(actor, delivered.Invocation, delivered.Metadata,
                            scope.TimeProvider), ct).ConfigureAwait(false);
                }
                else
                {
                    var dispatch = await RequestDispatch.SendAsync(
                        scope.ActorValidator, scope.Bus, delivered.Request, delivered.ActorToken, delivered.Invocation, delivered.Metadata,
                        scope.TimeProvider, delivered.TraceContext, ct).ConfigureAwait(false);
                    if (!dispatch.WasDispatched)
                        PortiaTelemetry.RecordRunnerFault(nameof(RequestNotificationRunner), "actor validation failed", logger: _logger);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Nothing to abandon or redeliver at this transport's level; a failed dispatch
                // is simply lost. Continue processing later deliveries.
                PortiaTelemetry.RecordRunnerFault(nameof(RequestNotificationRunner), "unrecognized exception", ex, _logger);
            }
        }
    }
}
