using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cntryl.Portia;

/// <summary>
/// Consumes requests off a queue and dispatches each to the request bus, so a command's handler
/// is the same regardless of whether it originated over HTTP, a queue, or RPC.
/// </summary>
public sealed class QueueRunner
{
    readonly IRequestQueueConsumer _consumer;
    readonly IRequestBus? _bus;
    readonly IRequestActorValidator? _actorValidator;
    readonly IServiceScopeFactory? _scopeFactory;
    readonly ILogger<QueueRunner>? _logger;
    readonly QueueRunnerOptions? _options;
    readonly IQueuedRequestTerminalHandler? _terminalHandler;

    /// <summary>Creates a runner with explicitly owned application dependencies.</summary>
    /// <param name="consumer">The queue consumer.</param>
    /// <param name="bus">The request bus.</param>
    /// <param name="actorValidator">Re-validates each request's carried actor token — signature and
    /// expiry included — at the moment it's actually dequeued, not just at the moment it was
    /// enqueued.</param>
    /// <param name="logger">
    /// Reports a dropped or abandoned request even when nothing is listening to
    /// <see cref="PortiaTelemetry.ActivitySource" />. Supply it explicitly, or configure
    /// Microsoft.Extensions.Logging with at least one provider before resolving the runner through
    /// DI; a bare <c>ServiceCollection</c> registration does not create or emit logs.
    /// </param>
    public QueueRunner(IRequestQueueConsumer consumer, IRequestBus bus,
        IRequestActorValidator actorValidator, ILogger<QueueRunner>? logger = null)
    {
        _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _actorValidator = actorValidator ?? throw new ArgumentNullException(nameof(actorValidator));
        _logger = logger;
        _options = new QueueRunnerOptions();
    }

    /// <summary>Creates a runner that owns a fresh application scope for each delivery.</summary>
    /// <param name="consumer">The long-lived transport consumer.</param>
    /// <param name="scopeFactory">Creates each delivery's application scope.</param>
    /// <param name="logger">Reports failed deliveries.</param>
    public QueueRunner(IRequestQueueConsumer consumer, IServiceScopeFactory scopeFactory, ILogger<QueueRunner>? logger = null)
    {
        _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger;
    }

    /// <summary>Creates a runner with explicit terminal-failure policy.</summary>
    public QueueRunner(IRequestQueueConsumer consumer, IRequestBus bus, IRequestActorValidator actorValidator,
        QueueRunnerOptions options, IQueuedRequestTerminalHandler? terminalHandler, ILogger<QueueRunner>? logger = null)
        : this(consumer, bus, actorValidator, logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _terminalHandler = terminalHandler;
    }

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
            try
            {
                using var delivery = CancellationTokenSource.CreateLinkedTokenSource(ct, queued.ReservationCancellation);
                await using var scope = _scopeFactory?.CreateAsyncScope();
                var bus = scope?.ServiceProvider.GetRequiredService<IRequestBus>() ?? _bus!;
                var actorValidator = scope?.ServiceProvider.GetRequiredService<IRequestActorValidator>() ?? _actorValidator!;
                var dispatch = await RequestDispatch.SendAsync(
                    actorValidator, bus, queued.Request, queued.ActorToken, queued.Invocation, queued.Metadata, scope?.ServiceProvider.GetService<TimeProvider>(), queued.TraceContext, delivery.Token).ConfigureAwait(false);

                if (!dispatch.WasDispatched)
                {
                    // Actor validation failed (e.g. an expired token) — retrying won't make it
                    // valid, so the request is dropped rather than redelivered forever.
                    PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), "actor validation failed", logger: _logger);
                    await queued.CompleteAsync(ct).ConfigureAwait(false);
                }
                else if (dispatch.Outcome.IsSuccess || dispatch.Outcome.Error is { IsTransient: false })
                {
                    await queued.CompleteAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    if (!await CompleteTerminalAsync(queued, dispatch.Outcome.Error, null, ct).ConfigureAwait(false))
                        await queued.AbandonAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // An unrecognized exception's retriability is unknown; abandoning (rather than
                // silently dropping the request) is the safer default.
                PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), "unrecognized exception", ex, _logger);
                if (!await CompleteTerminalAsync(queued, null, ex, ct).ConfigureAwait(false))
                    await queued.AbandonAsync(ct).ConfigureAwait(false);
            }
        }
    }

    async ValueTask<bool> CompleteTerminalAsync(IQueuedRequest queued, RequestError? error, Exception? exception, CancellationToken ct)
    {
        await using var scope = _scopeFactory?.CreateAsyncScope();
        var options = scope?.ServiceProvider.GetService<IOptions<QueueRunnerOptions>>()?.Value ?? _options ?? new QueueRunnerOptions();
        var handler = scope?.ServiceProvider.GetService<IQueuedRequestTerminalHandler>() ?? _terminalHandler;
        if (options.TerminalAttempt is not { } terminal || queued.Attempt < terminal || handler is null)
            return false;
        try
        {
            await handler.HandleAsync(new QueuedRequestFailureContext(
                queued.Request, queued.Metadata, queued.Invocation, queued.Attempt, error, exception), ct).ConfigureAwait(false);
            await queued.CompleteAsync(ct).ConfigureAwait(false);
        }
        catch (Exception callbackException) when (!ct.IsCancellationRequested)
        {
            PortiaTelemetry.RecordRunnerFault(nameof(QueueRunner), "terminal callback or acknowledgment failed", callbackException, _logger);
        }
        return true;
    }
}
