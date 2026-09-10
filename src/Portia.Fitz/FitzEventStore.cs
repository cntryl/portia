using System.Runtime.CompilerServices;
using System.Text;
using Cntryl.Fitz.Abstractions.Domains.Stream;
using Cntryl.Fitz.Errors;

namespace Cntryl.Portia;

/// <summary>
///     Persists aggregate event histories in Fitz streams.
/// </summary>
public sealed class FitzEventStore : IEventStore, IDomainEventNotifier
{
    const ulong ReadPageSize = 1024;
    readonly IDomainEventSerializer _serializer;

    readonly IStreamClient _streams;

    /// <summary>
    ///     Creates a Fitz-backed event store.
    /// </summary>
    /// <param name="streams">The Fitz stream client.</param>
    /// <param name="serializer">The durable domain-event serializer.</param>
    public FitzEventStore(IStreamClient streams, IDomainEventSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(serializer);

        _streams = streams;
        _serializer = serializer;
    }

    /// <inheritdoc />
    public async ValueTask<IDomainEventSubscription> SubscribeAsync(
        EventStreamPattern pattern,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var subscription = await _streams.SubscribeAsync(pattern.ToString(), ct).ConfigureAwait(false);
        return new FitzDomainEventSubscription(subscription);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DomainEventRecord> ReadAsync(
        EventStreamAddress stream,
        ulong fromOffset = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var telemetryStarted = PortiaTelemetry.StartTimestamp();
        var telemetryOutcome = "fault";
        var telemetryCount = 0;
        try
        {
            ArgumentNullException.ThrowIfNull(stream);
            var route = stream.ToString();
            var startOffset = fromOffset;
            var nextOffset = fromOffset;
            var eventIds = new HashSet<Uuid>();

            while (true)
            {
                var page = await _streams.ReadPageAsync(
                    route,
                    startOffset,
                    ReadPageSize,
                    ct: ct).ConfigureAwait(false);

                foreach (var item in page.Items)
                {
                    var record = item.Record ?? throw new InvalidOperationException(
                        $"Fitz stream '{route}' contains a gap at offset '{item.Offset}'.");
                    var expectedOffset = nextOffset;

                    if (record.Offset != expectedOffset)
                    {
                        throw new InvalidOperationException(
                            $"Fitz stream offset '{record.Offset}' does not match expected offset '{expectedOffset}'.");
                    }

                    var ev = _serializer.Deserialize(record.Body);
                    DomainEventInvariants.ValidateEvent(ev, eventIds);
                    telemetryCount++;
                    yield return new DomainEventRecord(stream, ev, record.Offset,
                        record.AreaOffset, record.RealmOffset);
                    nextOffset++;
                }

                if (!page.Cursor.HasMore)
                {
                    telemetryOutcome = "success";
                    yield break;
                }
                else
                {
                    startOffset = checked(page.Cursor.LastResourceOffset + 1);
                }
            }
        }
        finally
        {
            PortiaTelemetry.EventStoreFinished(telemetryStarted, "read", "stream",
                ct.IsCancellationRequested ? "canceled" : telemetryOutcome, telemetryCount);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DomainEventRecord> ReadAsync(
        EventStreamPattern pattern,
        ulong fromOffset = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var telemetryStarted = PortiaTelemetry.StartTimestamp();
        var telemetryOutcome = "fault";
        var telemetryCount = 0;
        try
        {
            ArgumentNullException.ThrowIfNull(pattern);
            var route = pattern.ToString();
            var startOffset = fromOffset;
            var nextOffset = fromOffset;

            while (true)
            {
                var page = await _streams.ReadPageAsync(
                    route,
                    startOffset,
                    ReadPageSize,
                    ct: ct).ConfigureAwait(false);

                foreach (var item in page.Items)
                {
                    var record = item.Record ?? throw new InvalidOperationException(
                        $"Fitz stream pattern '{route}' contains a gap at offset '{item.Offset}'.");
                    var metadata = record.Metadata ?? throw new InvalidOperationException(
                        "A Portia Fitz record does not contain its concrete stream route.");
                    var stream = EventStreamAddress.Parse(Encoding.UTF8.GetString(metadata));

                    if (!FitzEventStreamPatternOffsets.Matches(stream, pattern))
                        throw new InvalidOperationException($"Stream '{stream}' does not match pattern '{pattern}'.");

                    var areaOffset = record.AreaOffset;
                    var realmOffset = record.RealmOffset;
                    var scopeOffset =
                        FitzEventStreamPatternOffsets.GetPatternOffset(pattern, record.Offset, areaOffset, realmOffset);

                    if (scopeOffset != nextOffset)
                    {
                        throw new InvalidOperationException(
                            $"Fitz pattern offset '{scopeOffset}' does not match expected offset '{nextOffset}'.");
                    }

                    var ev = _serializer.Deserialize(record.Body);
                    DomainEventValidation.Validate(ev);
                    telemetryCount++;
                    yield return new DomainEventRecord(stream, ev, record.Offset, areaOffset, realmOffset);
                    nextOffset++;
                }

                if (!page.Cursor.HasMore)
                {
                    telemetryOutcome = "success";
                    yield break;
                }
                else
                {
                    startOffset = FitzEventStreamPatternOffsets.GetNextOffset(pattern, page.Cursor);
                }
            }
        }
        finally
        {
            PortiaTelemetry.EventStoreFinished(telemetryStarted, "read", "pattern",
                ct.IsCancellationRequested ? "canceled" : telemetryOutcome, telemetryCount);
        }
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(
        EventStreamAddress stream,
        ulong expectedStreamPosition,
        IReadOnlyList<DomainEvent> events,
        CancellationToken ct = default)
    {
        var telemetryStarted = PortiaTelemetry.StartTimestamp();
        var telemetryOutcome = "fault";
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(events);
        ct.ThrowIfCancellationRequested();
        DomainEventValidation.ValidateBatch(events);

        if (events.Count == 0)
        {
            PortiaTelemetry.EventStoreFinished(telemetryStarted, "append", "stream", "success");
            return;
        }

        var session = await _streams.BeginAsync(stream.ToString(), ct: ct).ConfigureAwait(false);
        var streamMetadata = Encoding.UTF8.GetBytes(stream.ToString());
        var failed = false;

        try
        {
            for (var index = 0; index < events.Count; index++)
            {
                var ev = events[index];
                var expectedOffset = checked(expectedStreamPosition + (ulong)index);
                _ = await session.AppendAsync(
                    expectedOffset,
                    _serializer.Serialize(ev),
                    streamMetadata,
                    ct: ct).ConfigureAwait(false);
            }

            await session.CommitAsync(ct).ConfigureAwait(false);
            telemetryOutcome = "success";
        }
        catch (Exception ex)
        {
            failed = true;
            await RollbackAsync(session).ConfigureAwait(false);

            if (ex is StreamException { DomainCode: 2001 })
            {
                throw new EventStreamConcurrencyException(
                    $"Stream '{stream}' is not at the expected physical stream position.", ex);
            }
            else
            {
                throw;
            }
        }
        finally
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch when (failed)
            {
                // Cleanup must not replace the append/commit failure seen by the caller.
            }

            PortiaTelemetry.EventStoreFinished(telemetryStarted, "append", "stream",
                ct.IsCancellationRequested ? "canceled" : telemetryOutcome,
                telemetryOutcome == "success" ? events.Count : 0);
        }
    }

    static async Task RollbackAsync(IStreamSession session)
    {
        try
        {
            await session.RollbackAsync().ConfigureAwait(false);
        }
        catch
        {
            // Preserve the append or commit failure that caused the rollback.
        }
    }

    sealed class FitzDomainEventSubscription : IDomainEventSubscription
    {
        readonly IAsyncEnumerator<StreamCommitEvent> _notifications;
        readonly CancellationTokenSource _stop = new();
        readonly StreamSubscription _subscription;
        Task<bool>? _pending;

        public FitzDomainEventSubscription(StreamSubscription subscription)
        {
            _subscription = subscription;
            _notifications = subscription.GetAsyncEnumerator(_stop.Token);
        }

        public async ValueTask WaitAsync(CancellationToken ct = default)
        {
            _pending ??= _notifications.MoveNextAsync().AsTask();
            try
            {
                if (!await _pending.WaitAsync(ct).ConfigureAwait(false))
                    throw new InvalidOperationException("The Fitz stream subscription ended without cancellation.");
                _pending = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await _stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            if (_pending is not null)
            {
                try
                {
                    _ = await _pending.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                }
            }

            await _notifications.DisposeAsync().ConfigureAwait(false);
            await _subscription.DisposeAsync().ConfigureAwait(false);
            _stop.Dispose();
        }
    }
}
