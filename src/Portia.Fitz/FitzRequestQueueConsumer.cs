using System.Runtime.CompilerServices;
using Cntryl.Fitz.Abstractions.Domains.Queue;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>Reserves no-result requests and maintains their Fitz leases while processing.</summary>
/// <param name="queue">The long-lived Fitz queue client.</param>
/// <param name="serializer">Deserializes a payload inside the delivery's failure boundary.</param>
/// <param name="route">The concrete queue route.</param>
/// <param name="visibilityTimeoutSeconds">Reservation duration, renewed while owned.</param>
/// <param name="maxItemsPerReserve">Batch size; defaults to one for sequential processing.</param>
/// <param name="waitDuration">Maximum idle wait before an immediate reconciliation reserve.</param>
/// <param name="timeProvider">Schedules reservation renewal.</param>
/// <param name="logger">Reports reservation failures.</param>
public sealed class FitzRequestQueueConsumer(
    IQueueClient queue,
    IRequestDeserializer serializer,
    string route,
    ulong visibilityTimeoutSeconds = 30,
    int maxItemsPerReserve = 1,
    TimeSpan? waitDuration = null,
    TimeProvider? timeProvider = null,
    ILogger<FitzRequestQueueConsumer>? logger = null) : IRequestQueueConsumer
{
    readonly int _batchSize = maxItemsPerReserve > 0
        ? maxItemsPerReserve
        : throw new ArgumentOutOfRangeException(nameof(maxItemsPerReserve));

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
        await using var subscription = await _queue.SubscribeAsync(_route, ct).ConfigureAwait(false);
        using var notificationLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var notifications = subscription.GetAsyncEnumerator(notificationLifetime.Token);
        Task<bool>? pendingNotification = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var items = await _queue.ReserveAsync(_route, visibilityTimeoutSeconds, _batchSize, 0, ct)
                    .ConfigureAwait(false);
                var reservations = new List<FitzQueuedRequest>(items.Length);
                try
                {
                    foreach (var item in items)
                    {
                        reservations.Add(new FitzQueuedRequest(item, _serializer, visibilityTimeoutSeconds,
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
                    try
                    {
                        pendingNotification ??= notifications.MoveNextAsync().AsTask();
                        if (!await pendingNotification.WaitAsync(_notificationBackstop, ct).ConfigureAwait(false))
                        {
                            throw new InvalidOperationException(
                                "The Fitz queue subscription ended without cancellation.");
                        }

                        pendingNotification = null;
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }
        }
        finally
        {
            await notificationLifetime.CancelAsync().ConfigureAwait(false);
            if (pendingNotification is not null)
            {
                try
                {
                    _ = await pendingNotification.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (notificationLifetime.IsCancellationRequested)
                {
                }
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
        readonly IQueueReservedItem _item;
        readonly CancellationTokenSource _lost;
        readonly Lazy<DeserializedRequest> _payload;
        readonly Task _renewal;
        readonly CancellationTokenSource _stop;
        Exception? _renewalError;

        public FitzQueuedRequest(IQueueReservedItem item, IRequestDeserializer serializer, ulong leaseSeconds,
            TimeSpan interval,
            TimeProvider clock, ILogger? logger, CancellationToken ct)
        {
            _item = item;
            _payload = new Lazy<DeserializedRequest>(() => serializer.DeserializeEnvelope(item.Body));
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _lost = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _renewal = RenewAsync(leaseSeconds, interval, clock, logger);
        }

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
            }
        }

        public IRequest Request => _payload.Value.Request as IRequest ??
                                   throw new InvalidOperationException("Only no-result requests can be queued.");

        public string? Name => _payload.Value.Name;
        public RequestMetadata Metadata => _payload.Value.Metadata;
        public RequestTraceContext? TraceContext => _payload.Value.TraceContext;
        public RequestInvocation Invocation => new QueueInvocation(_item.Route, _item.Attempt);
        public string? ActorToken => _payload.Value.ActorToken;
        public uint Attempt => _item.Attempt;
        public CancellationToken ReservationCancellation => _lost.Token;

        public async ValueTask CompleteAsync(CancellationToken ct = default)
        {
            if (_renewalError is not null)
            {
                throw new InvalidOperationException("Cannot acknowledge a reservation whose renewal failed.",
                    _renewalError);
            }

            _lost.Token.ThrowIfCancellationRequested();
            try
            {
                await _item.CompleteAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                await StopRenewalAsync().ConfigureAwait(false);
            }
        }

        // Fitz owns expiration, redelivery and dead-letter policy. Do not acknowledge or republish.
        public ValueTask AbandonAsync(CancellationToken ct = default) => StopRenewalAsync();

        async Task RenewAsync(ulong leaseSeconds, TimeSpan interval, TimeProvider clock, ILogger? logger)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(interval, clock, _stop.Token).ConfigureAwait(false);
                    await _item.ExtendAsync(leaseSeconds, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _renewalError = ex;
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
