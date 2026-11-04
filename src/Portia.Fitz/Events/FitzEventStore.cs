using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
///     Persists aggregate event histories in Fitz streams.
/// </summary>
public sealed class FitzEventStore : IEventStore, IDomainEventNotifier, IDomainEventProgressReader
{
    const ulong ReadPageSize = 1024;
    const ulong ProgressReadByteBudget = 256 * 1024;
    readonly ILogger<FitzEventStore>? _logger;
    readonly IDomainEventSerializer _serializer;

    readonly IStreamClient _streams;

    /// <summary>
    ///     Creates a Fitz-backed event store.
    /// </summary>
    /// <param name="streams">The Fitz stream client.</param>
    /// <param name="serializer">The durable domain-event serializer.</param>
    public FitzEventStore(IStreamClient streams, IDomainEventSerializer serializer)
        : this(streams, serializer, null)
    {
    }

    /// <summary>
    ///     Creates a Fitz-backed event store with an optional logger for nonfatal session cleanup faults.
    /// </summary>
    /// <param name="streams">The Fitz stream client.</param>
    /// <param name="serializer">The durable domain-event serializer.</param>
    /// <param name="logger">Reports cleanup failures after a confirmed commit.</param>
    public FitzEventStore(IStreamClient streams, IDomainEventSerializer serializer,
        ILogger<FitzEventStore>? logger)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(serializer);

        _streams = streams;
        _serializer = serializer;
        _logger = logger;
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
    /// <remarks>
    ///     Samples at most 1,024 source entries plus one preceding-position probe without deserializing events.
    ///     Diagnostic reads request a 256 KiB provider byte budget. Resource metadata supplies
    ///     a source position; realm and area positions are known only when the sampled page is exhausted.
    ///     Larger backlogs report a proven lower bound rather than an estimated exact count.
    /// </remarks>
    public async ValueTask<EventStreamProgress> GetProgressAsync(EventStreamPattern pattern,
        EventCursor position, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.IsTenantTemplate)
            throw new ArgumentException("Bind the processor's tenant before observing source progress.", nameof(pattern));
        ct.ThrowIfCancellationRequested();
        var offset = Offset(position);

        if (pattern.Scope == EventStreamPatternScope.Resource)
        {
            var metadata = await _streams.MetadataAsync(pattern.ToString(), ct).ConfigureAwait(false);
            var head = metadata.RecordCount == 0 ? 0 : checked(metadata.LastOffset + 1);
            var readable = offset <= head && (metadata.RecordCount == 0 || offset >= metadata.FirstOffset);
            ulong? pending = readable && offset == head ? 0 : null;
            var lowerBound = pending;
            if (readable && head > offset)
            {
                // Metadata gives the source position, but cannot establish whether retention has
                // withheld an event inside those bounds. Verify a bounded range before counting it.
                var resourcePage = await _streams.ReadPageAsync(pattern.ToString(), offset,
                        Math.Min(head - offset, ReadPageSize), maxBytes: ProgressReadByteBudget, ct: ct)
                    .ConfigureAwait(false);
                var observed = InspectProgressRecords(pattern, offset, resourcePage.Items, exclusiveHead: head);
                lowerBound = observed.Count;
                if (observed.Contiguous && observed.Count == head - offset)
                    pending = observed.Count;
            }
            return new EventStreamProgress(position, Cursor(head), pending,
                offset <= head ? offset == head : null, DateTimeOffset.UtcNow, lowerBound);
        }

        // The published Fitz client exposes resource metadata, but not realm/area watermarks.
        // Inspect one bounded page without deserializing application payloads. An exhausted page
        // gives an exact backlog; a larger backlog stays unknown rather than scanning its history.
        var page = await _streams.ReadPageAsync(pattern.ToString(), offset, ReadPageSize,
                maxBytes: ProgressReadByteBudget, ct: ct)
            .ConfigureAwait(false);
        var records = InspectProgressRecords(pattern, offset, page.Items);
        if (page.Cursor.HasMore)
            return new EventStreamProgress(position, null, null, page.Items.Count == 0 ? null : false,
                DateTimeOffset.UtcNow, records.Count);
        if (page.Items.Count == 0)
            return await ObserveEmptyPatternAsync(pattern, position, offset, page.Cursor, ct).ConfigureAwait(false);

        var sourcePosition = FitzEventStreamPatternOffsets.GetNextOffset(pattern, page.Cursor);
        if (sourcePosition < records.NextOffset)
            throw new InvalidOperationException("Fitz returned a source cursor behind its records.");
        ulong? pendingEvents = records.Contiguous && sourcePosition == records.NextOffset ? records.Count : null;
        return new EventStreamProgress(position, Cursor(sourcePosition), pendingEvents, false,
            DateTimeOffset.UtcNow, records.Count);
    }

    async ValueTask<EventStreamProgress> ObserveEmptyPatternAsync(EventStreamPattern pattern,
        EventCursor position, ulong offset, StreamReadCursor cursor, CancellationToken ct)
    {
        var covered = FitzEventStreamPatternOffsets.GetLastOffset(pattern, cursor);
        if (covered > offset)
            return new EventStreamProgress(position, Cursor(checked(covered + 1)), null, false,
                DateTimeOffset.UtcNow, 0);
        if (offset == 0)
            return new EventStreamProgress(position, null, 0, true, DateTimeOffset.UtcNow, 0);

        // Empty reads initialize their cursor from the requested offset. Verify one preceding
        // position before calling a nonzero checkpoint caught up: the checkpoint may be ahead
        // of a restored or replaced source. This adds at most one source entry to the probe.
        var preceding = await _streams.ReadPageAsync(pattern.ToString(), offset - 1, 1,
            maxBytes: ProgressReadByteBudget, ct: ct).ConfigureAwait(false);
        var observed = InspectProgressRecords(pattern, offset - 1, preceding.Items, countFrom: offset);
        if (preceding.Cursor.HasMore)
            return new EventStreamProgress(position, null, null, observed.Count > 0 ? false : null,
                DateTimeOffset.UtcNow, observed.Count);
        var precedingCovered = FitzEventStreamPatternOffsets.GetLastOffset(pattern, preceding.Cursor);
        if (preceding.Items.Count == 0 && precedingCovered <= offset - 1)
            return new EventStreamProgress(position, null, null, null, DateTimeOffset.UtcNow);

        var head = checked(precedingCovered + 1);
        if (head < observed.NextOffset)
            throw new InvalidOperationException("Fitz returned a source cursor behind its records.");
        if (head == offset)
            return new EventStreamProgress(position, Cursor(head), 0, true, DateTimeOffset.UtcNow, 0);
        return new EventStreamProgress(position, Cursor(head), null, false, DateTimeOffset.UtcNow, observed.Count);
    }

    static (ulong Count, bool Contiguous, ulong NextOffset) InspectProgressRecords(EventStreamPattern pattern,
        ulong offset, IReadOnlyList<StreamReadItem> items, ulong? exclusiveHead = null, ulong? countFrom = null)
    {
        var next = offset;
        var count = 0UL;
        var contiguous = true;
        foreach (var item in items)
        {
            if (!FitzEventStreamPatternOffsets.Matches(EventStreamAddress.Parse(item.Route), pattern))
                throw new InvalidOperationException("Fitz returned a record outside the observed source pattern.");
            ulong first;
            ulong last;
            switch (item.Kind)
            {
                case StreamReadItemKind.Event when item.Record is { } record:
                    if (record.Route != item.Route)
                        throw new InvalidOperationException("Fitz returned conflicting source routes for one record.");
                    first = last = FitzEventStreamPatternOffsets.GetPatternOffset(pattern, record.Offset,
                        record.AreaOffset, record.RealmOffset);
                    break;
                case StreamReadItemKind.Filtered when item.Record is null:
                    first = last = item.Offset;
                    contiguous = false;
                    break;
                case StreamReadItemKind.FilteredRange when item.Record is null:
                    first = item.FromOffset;
                    last = item.ToOffset;
                    contiguous = false;
                    break;
                default:
                    throw new InvalidOperationException("Fitz returned a malformed source entry.");
            }
            if (first < next || last < first)
                throw new InvalidOperationException("Fitz returned nonmonotonic source progress.");
            if (first != next)
                contiguous = false;
            if (item.Kind == StreamReadItemKind.Event && first >= (countFrom ?? offset) &&
                (exclusiveHead is null || first < exclusiveHead))
                count++;
            next = checked(last + 1);
        }
        return (count, contiguous, next);
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
            var committed = false;
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

                await session.CommitAsync(StreamCommitMode.Sync, ct).ConfigureAwait(false);
                committed = true;
                telemetryCount = events.Count;
                telemetryOutcome = "success";
                if (ProcessingObservation.IsEnabled)
                    foreach (var ev in events)
                        ProcessingObservation.Record("event", stream.ToString(), ev.Metadata.EventId.ToString(),
                            ev.Metadata.ExecutionId?.ToString(), "committed");
            }
            catch (Exception exception) when (committed)
            {
                // The durable outcome is already known. A post-commit observation failure must not
                // make callers retry an append that Fitz has confirmed.
                PortiaTelemetry.RecordRunnerFault(nameof(FitzEventStore), RunnerFaultStage.Cleanup, exception,
                    _logger);
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
                catch (Exception exception) when (committed)
                {
                    // Fitz confirmed the commit. Report cleanup failure separately so callers can
                    // clear their pending batch instead of retrying an append that already exists.
                    PortiaTelemetry.RecordRunnerFault(nameof(FitzEventStore), RunnerFaultStage.Cleanup,
                        exception, _logger);
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
