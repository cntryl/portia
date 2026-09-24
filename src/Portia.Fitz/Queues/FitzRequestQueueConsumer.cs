using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>Reserves no-result requests and maintains their Fitz leases while processing.</summary>
/// <param name="queue">The long-lived Fitz queue client.</param>
/// <param name="serializer">Deserializes a payload inside the delivery's failure boundary.</param>
/// <param name="route">The concrete queue route.</param>
/// <param name="catalog">Validates the request's declared transport capability.</param>
/// <param name="visibilityTimeoutSeconds">Reservation duration, renewed while owned.</param>
/// <param name="waitDuration">Maximum idle wait before an immediate reconciliation reserve.</param>
/// <param name="timeProvider">Schedules reservation renewal.</param>
/// <param name="logger">Reports reservation failures.</param>
public sealed class FitzRequestQueueConsumer(
    IQueueClient queue,
    IRequestDeserializer serializer,
    string route,
    RequestTransportCatalog catalog,
    ulong visibilityTimeoutSeconds = 30,
    TimeSpan? waitDuration = null,
    TimeProvider? timeProvider = null,
    ILogger<FitzRequestQueueConsumer>? logger = null) : IRequestQueueConsumer
{
    readonly RequestTransportCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    readonly TimeSpan _notificationBackstop = GetNotificationBackstop(waitDuration);
    readonly IQueueClient _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    readonly TimeSpan _renewalInterval = GetRenewalInterval(visibilityTimeoutSeconds);

    readonly string _route = string.IsNullOrWhiteSpace(route)
        ? throw new ArgumentException("A queue route cannot be empty.", nameof(route))
        : route;

    readonly IRequestDeserializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

    /// <inheritdoc />
    public async IAsyncEnumerable<IQueuedRequest> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var wakeups = await FitzWakeupSubscription<QueueAvailabilityEvent>.SubscribeAsync(
                async token => await _queue.SubscribeAsync(_route, token).ConfigureAwait(false),
                "The Fitz queue subscription ended without cancellation.", ct)
            .ConfigureAwait(false);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // The reserve below answers every wake-up that arrived before it, so they are consumed
            // here. A consumer that never runs dry would otherwise never read them at all.
            await wakeups.DrainAsync(ct).ConfigureAwait(false);
            var items = await _queue.ReserveAsync(_route, TimeSpan.FromSeconds(visibilityTimeoutSeconds),
                    1, TimeSpan.Zero, ct)
                .ConfigureAwait(false);
            var reservations = new List<FitzQueuedRequest>(items.Length);
            try
            {
                foreach (var item in items)
                {
                    reservations.Add(new FitzQueuedRequest(item, _serializer, _catalog, visibilityTimeoutSeconds,
                        _renewalInterval, _clock, logger, ct));
                }

                foreach (var reservation in reservations)
                    yield return reservation;
            }
            finally
            {
                foreach (var reservation in reservations)
                    await reservation.DisposeAsync().ConfigureAwait(false);
            }

            if (items.Length == 0)
            {
                // An idle queue is the ordinary state, so the backstop elapsing is ordinary
                // too. Waiting on it by catching a TimeoutException threw once per idle poll,
                // per consumer, forever — noise in any first-chance exception view and a cost
                // paid for a condition that is not exceptional. A wake-up that wins is consumed
                // by the next drain.
                using var backstop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var elapsed = Task.Delay(_notificationBackstop, _clock, backstop.Token);
                _ = await Task.WhenAny(wakeups.Next, elapsed).ConfigureAwait(false);

                // Whichever lost is cancelled rather than left to fire later.
                await backstop.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    static TimeSpan GetRenewalInterval(ulong leaseSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfZero(leaseSeconds);
        return TimeSpan.FromSeconds(Math.Min(leaseSeconds / 2d, TimeSpan.FromDays(1).TotalSeconds));
    }

    static TimeSpan GetNotificationBackstop(TimeSpan? waitDuration)
    {
        var interval = waitDuration ?? TimeSpan.FromSeconds(5);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero, nameof(waitDuration));
        return interval;
    }

    sealed class FitzQueuedRequest : IQueuedRequest, IAsyncDisposable
    {
        readonly Lazy<DeserializedRequest> _envelope;
        readonly IQueueReservedItem _item;
        readonly CancellationTokenSource _lost;
        readonly Task _renewal;
        readonly Lazy<IRequest> _request;
        readonly CancellationTokenSource _stop;
        Exception? _renewalError;

        public FitzQueuedRequest(IQueueReservedItem item, IRequestDeserializer serializer,
            RequestTransportCatalog catalog, ulong leaseSeconds,
            TimeSpan interval,
            TimeProvider clock, ILogger? logger, CancellationToken ct)
        {
            _item = item;
            _envelope = new Lazy<DeserializedRequest>(() => serializer.DeserializeEnvelope(item.Body));
            _request = new Lazy<IRequest>(() => Validate(_envelope.Value.Request, catalog));
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _lost = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _renewal = RenewAsync(leaseSeconds, interval, clock, logger);
        }

        // Fitz keeps a disconnect listener, and with it the body, alive for every item that is
        // neither completed nor disposed, so an abandoned or faulted delivery must release its
        // item once renewal can no longer touch it. Disposing a completed item is a no-op.
        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopRenewalAsync().ConfigureAwait(false);
            }
            finally
            {
                _stop.Dispose();
                _lost.Dispose();
                await _item.DisposeAsync().ConfigureAwait(false);
            }
        }

        public IRequest Request => _request.Value;

        public string? Name => _envelope.Value.Name;
        public RequestMetadata Metadata => _envelope.Value.Metadata;
        public RequestTraceContext? TraceContext => _envelope.Value.TraceContext;

        public RequestInvocation Invocation => new QueueInvocation(_item.Route, _item.Attempt)
        {
            MessagingSystem = "fitz"
        };

        public string? ActorToken => _envelope.Value.ActorToken;
        public uint Attempt => _item.Attempt;
        public bool SupportsDurableAttempts => _item.Attempt != QueueItem.AttemptUnavailable;
        public CancellationToken ReservationCancellation => _lost.Token;

        public async ValueTask CompleteAsync(CancellationToken ct = default)
        {
            // Renewal stops before the acknowledgment rather than after it. Fitz moves the item to
            // "completing" when COMPLETE starts and then rejects EXTEND with ITEM_CLOSED, and an
            // EXTEND still in flight can reach the broker after COMPLETE retired the lease token;
            // either would report a lost reservation for a delivery being acknowledged. Nothing is
            // given up: Fitz rejects a COMPLETE whose lease expired, so it must fit inside the lease
            // regardless, and the last renewal left at least half of one.
            await StopRenewalAsync().ConfigureAwait(false);

            // Written by the renewal loop, so the read has to be ordered: acknowledging a
            // reservation whose lease was already lost is exactly the outcome this check exists to
            // prevent. It runs after renewal stopped, so a renewal that failed on its way out counts.
            if (Volatile.Read(ref _renewalError) is { } renewalError)
            {
                throw new InvalidOperationException("Cannot acknowledge a reservation whose renewal failed.",
                    renewalError);
            }

            _lost.Token.ThrowIfCancellationRequested();
            await _item.CompleteAsync(ct).ConfigureAwait(false);
        }

        // Fitz owns expiration, redelivery and dead-letter policy. Do not acknowledge or republish.
        public ValueTask AbandonAsync(CancellationToken ct = default) => StopRenewalAsync();

        static IRequest Validate(IRequestBase requestBase, RequestTransportCatalog catalog)
        {
            var request = requestBase as IRequest
                          ?? throw new InvalidOperationException("Only no-result requests can be queued.");
            if (!catalog.Get(request.GetType()).Transports.Contains(RequestTransportId.Queue))
                throw new InvalidRequestTransportException(request, RequestTransportId.Queue);
            return request;
        }

        async Task RenewAsync(ulong leaseSeconds, TimeSpan interval, TimeProvider clock, ILogger? logger)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(interval, clock, _stop.Token).ConfigureAwait(false);
                    await _item.ExtendAsync(TimeSpan.FromSeconds(leaseSeconds), _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _renewalError, ex);
                PortiaTelemetry.RecordRunnerFault(nameof(FitzRequestQueueConsumer), RunnerFaultStage.Renewal, ex,
                    logger);
                try
                {
                    await _lost.CancelAsync().ConfigureAwait(false);
                }
                catch (Exception callbackError)
                {
                    PortiaTelemetry.RecordRunnerFault(nameof(FitzRequestQueueConsumer), RunnerFaultStage.Cleanup,
                        callbackError, logger);
                }
            }
        }

        async ValueTask StopRenewalAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await _renewal.ConfigureAwait(false);
        }
    }
}
