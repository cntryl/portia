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
/// <param name="waitDuration">Long-poll duration, rounded up to seconds; defaults to five seconds.</param>
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
    readonly IQueueClient _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    readonly IRequestDeserializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    readonly TimeSpan _renewalInterval = GetRenewalInterval(visibilityTimeoutSeconds);
    readonly int _batchSize = maxItemsPerReserve > 0 ? maxItemsPerReserve : throw new ArgumentOutOfRangeException(nameof(maxItemsPerReserve));
    readonly int _waitSeconds = GetWaitSeconds(waitDuration);
    readonly string _route = string.IsNullOrWhiteSpace(route)
        ? throw new ArgumentException("A queue route cannot be empty.", nameof(route)) : route;

    /// <inheritdoc />
    public async IAsyncEnumerable<IQueuedRequest> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var items = await _queue.ReserveAsync(_route, visibilityTimeoutSeconds, _batchSize, _waitSeconds, ct).ConfigureAwait(false);
            var reservations = new List<FitzQueuedRequest>(items.Length);
            try
            {
                // Explicit larger batches also retain leases while awaiting sequential execution.
                foreach (var item in items)
                    reservations.Add(new FitzQueuedRequest(item, _serializer, visibilityTimeoutSeconds, _renewalInterval, _clock, logger, ct));
                foreach (var reservation in reservations)
                    yield return reservation;
            }
            finally
            {
                foreach (var reservation in reservations)
                    await reservation.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    static TimeSpan GetRenewalInterval(ulong leaseSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfZero(leaseSeconds);
        return TimeSpan.FromSeconds(Math.Min(leaseSeconds / 2d, TimeSpan.FromDays(1).TotalSeconds));
    }

    static int GetWaitSeconds(TimeSpan? waitDuration)
    {
        var duration = waitDuration ?? TimeSpan.FromSeconds(5);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero, nameof(waitDuration));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(duration.TotalSeconds, int.MaxValue, nameof(waitDuration));
        return (int)Math.Ceiling(duration.TotalSeconds);
    }

    sealed class FitzQueuedRequest : IQueuedRequest, IAsyncDisposable
    {
        readonly IQueueReservedItem _item;
        readonly Lazy<(IRequest Request, string? ActorToken)> _payload;
        readonly CancellationTokenSource _stop;
        readonly CancellationTokenSource _lost;
        readonly Task _renewal;
        Exception? _renewalError;

        public FitzQueuedRequest(IQueueReservedItem item, IRequestDeserializer serializer, ulong leaseSeconds, TimeSpan interval,
            TimeProvider clock, ILogger? logger, CancellationToken ct)
        {
            _item = item;
            _payload = new Lazy<(IRequest, string?)>(() =>
            {
                var (request, actorToken) = serializer.DeserializeRequest(item.Body);
                return (request as IRequest ?? throw new InvalidOperationException("Only no-result requests can be queued."), actorToken);
            });
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _lost = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _renewal = RenewAsync(leaseSeconds, interval, clock, logger);
        }

        public IRequest Request => _payload.Value.Request;
        public string? ActorToken => _payload.Value.ActorToken;
        public uint Attempt => _item.Attempt;
        public CancellationToken ReservationCancellation => _lost.Token;

        public async ValueTask CompleteAsync(CancellationToken ct = default)
        {
            if (_renewalError is not null)
                throw new InvalidOperationException("Cannot acknowledge a reservation whose renewal failed.", _renewalError);
            _lost.Token.ThrowIfCancellationRequested();
            try { await _item.CompleteAsync(ct).ConfigureAwait(false); }
            finally { await StopRenewalAsync().ConfigureAwait(false); }
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
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _renewalError = ex;
                PortiaTelemetry.RecordRunnerFault(nameof(FitzRequestQueueConsumer), "reservation renewal failed", ex, logger);
                await _lost.CancelAsync().ConfigureAwait(false);
            }
        }

        async ValueTask StopRenewalAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await _renewal.ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            try { await StopRenewalAsync().ConfigureAwait(false); }
            finally
            {
                _stop.Dispose();
                _lost.Dispose();
            }
        }
    }
}
