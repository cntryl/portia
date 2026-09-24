using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

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
        var route = pattern.ToString();
        var wakeups = await FitzWakeupSubscription<StreamCommitEvent>.SubscribeAsync(
                async token => await _streams.SubscribeAsync(route, token).ConfigureAwait(false),
                "The Fitz stream subscription ended without cancellation.", ct)
            .ConfigureAwait(false);
        return new FitzDomainEventSubscription(wakeups);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<DomainEventRecord> ReadAsync(
        EventStreamAddress stream,
        ulong fromOffset,
        CancellationToken ct) =>
        ReadPagesAsync(
            stream,
            nameof(stream),
            "stream",
            () => (stream.ToString(), fromOffset),
            (item, nextOffset) =>
            {
                var record = item.Record ?? throw new InvalidOperationException(
                    $"A Fitz stream in area '{stream.Area}' contains a gap at offset '{item.Offset}'.");
                var expectedOffset = nextOffset;
                if (record.Offset != expectedOffset)
                    throw new InvalidOperationException(
                        $"Fitz stream offset '{record.Offset}' does not match expected offset '{expectedOffset}'.");
                var ev = _serializer.Deserialize(record.Body);
                DomainEventValidation.Validate(ev);
                return new DomainEventRecord(stream, ev, record.Offset, Cursor(checked(record.Offset + 1)));
            },
            pageCursor => checked(pageCursor.LastResourceOffset + 1),
            ct);

    /// <inheritdoc />
    public IAsyncEnumerable<DomainEventRecord> ReadAsync(
        EventStreamPattern pattern,
        EventCursor cursor,
        CancellationToken ct) =>
        ReadPagesAsync(
            pattern,
            nameof(pattern),
            "pattern",
            () => (pattern.ToString(), Offset(cursor)),
            (item, nextOffset) =>
            {
                var record = item.Record ?? throw new InvalidOperationException(
                    $"A Fitz stream pattern read contains a gap at offset '{item.Offset}'.");
                var metadata = record.Metadata ?? throw new InvalidOperationException(
                    "A Portia Fitz record does not contain its concrete stream route.");
                var stream = EventStreamAddress.Parse(Encoding.UTF8.GetString(metadata.Span));
                if (stream != EventStreamAddress.Parse(record.Route))
                    throw new InvalidOperationException(
                        $"The metadata stream of the record at offset '{record.Offset}' does not match its Fitz route.");
                if (!FitzEventStreamPatternOffsets.Matches(stream, pattern))
                    throw new InvalidOperationException(
                        $"A stream in area '{stream.Area}' does not match the pattern it was read through.");
                var areaOffset = record.AreaOffset;
                var realmOffset = record.RealmOffset;
                var scopeOffset = FitzEventStreamPatternOffsets.GetPatternOffset(
                    pattern, record.Offset, areaOffset, realmOffset);
                if (scopeOffset != nextOffset)
                    throw new InvalidOperationException(
                        $"Fitz pattern offset '{scopeOffset}' does not match expected offset '{nextOffset}'.");
                var ev = _serializer.Deserialize(record.Body);
                DomainEventValidation.Validate(ev);
                return new DomainEventRecord(stream, ev, record.Offset, Cursor(checked(scopeOffset + 1)));
            },
            pageCursor => FitzEventStreamPatternOffsets.GetNextOffset(pattern, pageCursor),
            ct);

    /// <inheritdoc />
    public async ValueTask AppendAsync(
        EventStreamAddress stream,
        ulong expectedStreamPosition,
        IReadOnlyList<DomainEvent> events,
        CancellationToken ct = default)
    {
        var telemetryStarted = PortiaTelemetry.StartTimestamp();
        var telemetryOutcome = "fault";
        var telemetryCount = 0;

        try
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(events);
            ct.ThrowIfCancellationRequested();
            DomainEventValidation.ValidateBatch(events);

            if (events.Count == 0)
            {
                telemetryOutcome = "success";
                return;
            }

            IStreamSession session;
            try
            {
                session = await _streams.BeginAsync(stream.ToString(), ct: ct).ConfigureAwait(false);
            }
            catch (StreamException ex) when (
                ex.DomainCode == FitzErrorCodes.StreamSessionAlreadyActive)
            {
                throw new EventStreamConcurrencyException(
                    $"A stream in area '{stream.Area}' already has an active append session.", ex);
            }
            var failed = false;
            try
            {
                var streamMetadata = Encoding.UTF8.GetBytes(stream.ToString());
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
                telemetryCount = events.Count;
                telemetryOutcome = "success";
            }
            catch (Exception ex)
            {
                failed = true;
                await RollbackAsync(session).ConfigureAwait(false);

                if (ex is StreamException { DomainCode: FitzErrorCodes.StreamConcurrencyConflict })
                {
                    throw new EventStreamConcurrencyException(
                        $"A stream in area '{stream.Area}' is not at the expected physical stream position "
                        + $"'{expectedStreamPosition}'.", ex);
                }

                throw;
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
            }
        }
        catch (Exception exception)
        {
            telemetryOutcome = PortiaTelemetry.ExceptionOutcome(exception, ct);
            throw;
        }
        finally
        {
            PortiaTelemetry.EventStoreFinished(telemetryStarted, "append", "stream",
                telemetryOutcome, telemetryCount);
        }
    }

    // Both reads share this page loop. Only a null source, record mapping, and next-page offset
    // math record a fault; resolving the initial route and offset leaves the outcome untouched.
    async IAsyncEnumerable<DomainEventRecord> ReadPagesAsync(
        object? source,
        string sourceName,
        string telemetryTarget,
        Func<(string Route, ulong StartOffset)> open,
        Func<StreamReadItem, ulong, DomainEventRecord> map,
        Func<StreamReadCursor, ulong> nextStartOffset,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var telemetryStarted = PortiaTelemetry.StartTimestamp();
        var telemetryOutcome = "success";
        var telemetryCount = 0;
        try
        {
            if (source is null)
            {
                telemetryOutcome = "fault";
                throw new ArgumentNullException(sourceName);
            }

            var (route, startOffset) = open();
            var nextOffset = startOffset;
            while (true)
            {
                StreamReadPage page;
                try
                {
                    page = await _streams.ReadPageAsync(route, startOffset, ReadPageSize, ct: ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    telemetryOutcome = "canceled";
                    throw;
                }
                catch
                {
                    telemetryOutcome = "fault";
                    throw;
                }

                foreach (var item in page.Items)
                {
                    DomainEventRecord result;
                    try
                    {
                        result = map(item, nextOffset);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        telemetryOutcome = "canceled";
                        throw;
                    }
                    catch
                    {
                        telemetryOutcome = "fault";
                        throw;
                    }

                    telemetryCount++;
                    yield return result;
                    nextOffset++;
                }

                if (!page.Cursor.HasMore)
                {
                    yield break;
                }
                else
                {
                    try
                    {
                        startOffset = nextStartOffset(page.Cursor);
                    }
                    catch
                    {
                        telemetryOutcome = "fault";
                        throw;
                    }
                }
            }
        }
        finally
        {
            PortiaTelemetry.EventStoreFinished(telemetryStarted, "read", telemetryTarget,
                telemetryOutcome, telemetryCount);
        }
    }

    static EventCursor Cursor(ulong offset) => new(offset.ToString(CultureInfo.InvariantCulture));

    static ulong Offset(EventCursor cursor) => cursor == EventCursor.Start
        ? 0
        : ulong.TryParse(cursor.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            ? offset
            : throw new ArgumentException("The event cursor was not issued by the Fitz event store.", nameof(cursor));

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

    // The token bounds one wait, not the subscription: a caller that polls with a backstop must
    // still receive the next commit. Only disposal ends the subscription. A workload that keeps
    // finding work skips its waits, so each wait drains every commit buffered meanwhile.
    sealed class FitzDomainEventSubscription(FitzWakeupSubscription<StreamCommitEvent> wakeups)
        : IDomainEventSubscription
    {
        public ValueTask WaitAsync(CancellationToken ct = default) => wakeups.WaitAsync(ct);

        public ValueTask DisposeAsync() => wakeups.DisposeAsync();
    }
}
