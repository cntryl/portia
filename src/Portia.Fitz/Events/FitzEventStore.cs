using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
    readonly Lock _subscriptionsGate = new();
    readonly Dictionary<string, SharedWakeupSubscription> _subscriptions = [];

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
    public FitzEventStore(
        IStreamClient streams,
        IDomainEventSerializer serializer,
        ILogger<FitzEventStore>? logger
    )
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
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var route = pattern.ToString();

        while (true)
        {
            SharedWakeupSubscription.Subscription subscription;
            SharedWakeupSubscription? start = null;
            Task? closing = null;

            lock (_subscriptionsGate)
            {
                if (_subscriptions.TryGetValue(route, out var wakeups))
                {
                    if (!wakeups.TrySubscribe(out subscription, out closing) && closing is null)
                    {
                        wakeups = new SharedWakeupSubscription(this, route);
                        _subscriptions[route] = wakeups;
                        subscription = wakeups.Subscribe();
                        start = wakeups;
                    }
                }
                else
                {
                    wakeups = new SharedWakeupSubscription(this, route);
                    _subscriptions[route] = wakeups;
                    subscription = wakeups.Subscribe();
                    start = wakeups;
                }
            }

            if (closing is not null)
            {
                await closing.WaitAsync(ct).ConfigureAwait(false);
                lock (_subscriptionsGate)
                {
                    if (_subscriptions.TryGetValue(route, out var wakeups) &&
                        wakeups.Closed == closing)
                    {
                        _ = _subscriptions.Remove(route);
                    }
                }

                continue;
            }

            start?.Start();

            try
            {
                await subscription.StartedAsync(ct).ConfigureAwait(false);
                return subscription;
            }
            catch
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<DomainEventRecord> ReadAsync(
        EventStreamAddress stream,
        ulong fromOffset,
        CancellationToken ct
    ) =>
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
            ct);

    /// <inheritdoc />
    public IAsyncEnumerable<DomainEventRecord> ReadAsync(
        EventStreamPattern pattern,
        EventCursor cursor,
        CancellationToken ct
    ) =>
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
            ct);

    /// <inheritdoc />
    /// <remarks>
    ///     Samples at most 1,024 source entries plus one preceding-position probe without deserializing events.
    ///     Diagnostic reads request a 256 KiB provider byte budget. Resource metadata supplies
    ///     a source position; realm and area positions are known only when the sampled page is exhausted.
    ///     Larger backlogs report a proven lower bound rather than an estimated exact count.
    /// </remarks>
    public async ValueTask<EventStreamProgress> GetProgressAsync(
        EventStreamPattern pattern,
        EventCursor position,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.IsTenantTemplate)
            throw new ArgumentException("Bind the processor's tenant before observing source progress.",
                nameof(pattern));

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
                var resourcePage = await ReadBatchAsync(pattern.ToString(), offset,
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
        var page = await ReadBatchAsync(pattern.ToString(), offset, ReadPageSize,
                maxBytes: ProgressReadByteBudget, ct: ct)
            .ConfigureAwait(false);
        var records = InspectProgressRecords(pattern, offset, page.Items);

        if (!page.CaughtUp)
            return new EventStreamProgress(position, null, null, page.Items.Count == 0 ? null : false,
                DateTimeOffset.UtcNow, records.Count);

        if (page.Items.Count == 0)
            return await ObserveEmptyPatternAsync(pattern, position, offset, page.NextOffset, ct).ConfigureAwait(false);

        var sourcePosition = page.NextOffset;

        if (sourcePosition < records.NextOffset)
            throw new InvalidOperationException("Fitz returned a source cursor behind its records.");

        ulong? pendingEvents = records.Contiguous && sourcePosition == records.NextOffset ? records.Count : null;
        return new EventStreamProgress(position, Cursor(sourcePosition), pendingEvents, false,
            DateTimeOffset.UtcNow, records.Count);
    }

    async ValueTask<EventStreamProgress> ObserveEmptyPatternAsync(
        EventStreamPattern pattern,
        EventCursor position,
        ulong offset,
        ulong nextOffset,
        CancellationToken ct
    )
    {
        if (nextOffset > offset)
            return new EventStreamProgress(position, Cursor(nextOffset), null, false,
                DateTimeOffset.UtcNow, 0);

        if (offset == 0)
            return new EventStreamProgress(position, null, 0, true, DateTimeOffset.UtcNow, 0);

        // Empty reads initialize their cursor from the requested offset. Verify one preceding
        // position before calling a nonzero checkpoint caught up: the checkpoint may be ahead
        // of a restored or replaced source. This adds at most one source entry to the probe.
        var preceding = await ReadBatchAsync(pattern.ToString(), offset - 1, 1,
            maxBytes: ProgressReadByteBudget, ct: ct).ConfigureAwait(false);
        var observed = InspectProgressRecords(pattern, offset - 1, preceding.Items, countFrom: offset);

        if (!preceding.CaughtUp)
            return new EventStreamProgress(position, null, null, observed.Count > 0 ? false : null,
                DateTimeOffset.UtcNow, observed.Count);

        var head = preceding.NextOffset;

        if (preceding.Items.Count == 0 && head <= offset - 1)
            return new EventStreamProgress(position, null, null, null, DateTimeOffset.UtcNow);


        if (head < observed.NextOffset)
            throw new InvalidOperationException("Fitz returned a source cursor behind its records.");

        if (head == offset)
            return new EventStreamProgress(position, Cursor(head), 0, true, DateTimeOffset.UtcNow, 0);

        return new EventStreamProgress(position, Cursor(head), null, false, DateTimeOffset.UtcNow, observed.Count);
    }

    static (ulong Count, bool Contiguous, ulong NextOffset) InspectProgressRecords(
        EventStreamPattern pattern,
        ulong offset,
        IReadOnlyList<StreamReadItem> items,
        ulong? exclusiveHead = null,
        ulong? countFrom = null
    )
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
        CancellationToken ct = default
    )
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

            var route = stream.ToString();
            var streamMetadata = Encoding.UTF8.GetBytes(route);
            var batch = new ReadOnlyMemory<byte>[events.Count];

            for (var index = 0; index < batch.Length; index++)
                batch[index] = _serializer.Serialize(events[index]);


            IStreamSession session;

            try
            {
                session = await _streams.BeginAsync(route, ct: ct).ConfigureAwait(false);
            }
            catch (StreamException ex) when (
                ex.DomainCode == FitzErrorCodes.StreamSessionAlreadyActive)
            {
                throw new EventStreamConcurrencyException(
                    $"A stream in area '{stream.Area}' already has an active append session.", ex);
            }


            Exception? writeFailure = null;
            var commitRequested = false;

            try
            {
                for (var index = 0; index < batch.Length; index++)
                {
                    var expectedOffset = checked(expectedStreamPosition + (ulong)index);
                    _ = await session.AppendAsync(
                        expectedOffset,
                        batch[index],
                        streamMetadata,
                        ct: ct).ConfigureAwait(false);
                }

                commitRequested = true;
                await session.CommitAsync(StreamCommitMode.Sync, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                writeFailure = ex;
                await RollbackAsync(session).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch when (writeFailure is not null)
                {
                    // Cleanup must not replace the append/commit failure seen by the caller.
                }
                catch (Exception exception)
                {
                    // Fitz confirmed the commit. Report cleanup failure separately so callers can
                    // clear their pending batch instead of retrying an append that already exists.
                    PortiaTelemetry.RecordRunnerFault(nameof(FitzEventStore), RunnerFaultStage.Cleanup,
                        exception, _logger);
                }
            }


            if (writeFailure is not null)
            {
                var conflict = writeFailure is StreamException { DomainCode: FitzErrorCodes.StreamConcurrencyConflict };
                var confirmed = !ct.IsCancellationRequested && (conflict || commitRequested) &&
                                await ConfirmsBatchAsync(route, streamMetadata, expectedStreamPosition, batch, ct)
                                    .ConfigureAwait(false);

                if (!confirmed)
                {
                    if (conflict)
                        throw new EventStreamConcurrencyException(
                            $"A stream in area '{stream.Area}' is not at the expected physical stream position "
                            + $"'{expectedStreamPosition}'.", writeFailure);


                    if (commitRequested && (writeFailure is not OperationCanceledException || !ct.IsCancellationRequested))
                        throw new EventStreamCommitUnknownException(
                            $"The commit outcome for a stream in area '{stream.Area}' could not be confirmed.",
                            writeFailure);

                    ExceptionDispatchInfo.Throw(writeFailure);
                }
            }

            telemetryCount = events.Count;
            telemetryOutcome = "success";

            if (ProcessingObservation.IsEnabled)
            {
                try
                {
                    foreach (var ev in events)
                        ProcessingObservation.Record("event", route, ev.Metadata.EventId.ToString(),
                            ev.Metadata.ExecutionId?.ToString(), "committed");
                }
                catch (Exception exception)
                {
                    PortiaTelemetry.RecordRunnerFault(nameof(FitzEventStore), RunnerFaultStage.Cleanup, exception,
                        _logger);
                }
            }
        }
        catch (Exception exception)
        {
            telemetryOutcome = exception is EventStreamCommitUnknownException
                ? "unknown"
                : PortiaTelemetry.ExceptionOutcome(exception, ct);
            throw;
        }
        finally
        {
            PortiaTelemetry.EventStoreFinished(telemetryStarted, "append", "stream",
                telemetryOutcome, telemetryCount);
        }
    }

    // Read the occupied offsets after releasing the append session. Only the complete encoded
    // batch at those offsets confirms a prior commit. A matching prefix cannot settle the save.
    async ValueTask<bool> ConfirmsBatchAsync(
        string route,
        byte[] streamMetadata,
        ulong offset,
        ReadOnlyMemory<byte>[] batch,
        CancellationToken ct
    )
    {
        try
        {
            var index = 0;

            while (index < batch.Length)
            {
                var limit = Math.Min(ReadPageSize, (ulong)(batch.Length - index));
                var page = await ReadBatchAsync(route, offset, limit, ct: ct).ConfigureAwait(false);

                if (page.Items.Count == 0 || (ulong)page.Items.Count > limit)
                    return false;


                foreach (var item in page.Items)
                {
                    if (item.Kind != StreamReadItemKind.Event || item.Record is not { } record ||
                        record.Offset != offset || record.Route != route ||
                        record.Metadata is not { } metadata || !metadata.Span.SequenceEqual(streamMetadata) ||
                        !record.Body.Span.SequenceEqual(batch[index].Span))
                        return false;

                    offset = checked(offset + 1);
                    index++;
                }

                if (index < batch.Length && page.CaughtUp)
                    return false;
            }


            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A confirmation read failure leaves the original write outcome unresolved.
            return false;
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
        [EnumeratorCancellation] CancellationToken ct
    )
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
            IAsyncEnumerator<StreamReadBatch> batches;

            try
            {
                batches = _streams.ReadAsync(route, startOffset, StreamReadMode.Replay,
                    new StreamReadOptions { BatchSize = ReadPageSize }, ct).GetAsyncEnumerator(ct);
            }
            catch (Exception ex)
            {
                telemetryOutcome = PortiaTelemetry.ExceptionOutcome(ex, ct);
                throw;
            }


            await using var ownedBatches = batches.ConfigureAwait(false);

            while (true)
            {
                StreamReadBatch page;

                try
                {
                    if (!await batches.MoveNextAsync().ConfigureAwait(false))
                        yield break;

                    page = batches.Current;
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


                if (page.CaughtUp)
                {
                    yield break;
                }


                if (page.NextOffset <= startOffset)
                    throw new InvalidOperationException("Fitz returned a batch without advancing its position.");

                startOffset = page.NextOffset;
            }
        }
        finally
        {
            PortiaTelemetry.EventStoreFinished(telemetryStarted, "read", telemetryTarget,
                telemetryOutcome, telemetryCount);
        }
    }

    async ValueTask<StreamReadBatch> ReadBatchAsync(
        string route,
        ulong offset,
        ulong limit,
        ulong? maxBytes = null,
        CancellationToken ct = default
    )
    {
        var options = new StreamReadOptions { BatchSize = limit, MaxBytes = maxBytes };
        return await _streams.ReadAsync(route, offset, StreamReadMode.Replay, options, ct)
            .FirstAsync(ct).ConfigureAwait(false);
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

    Task Release(SharedWakeupSubscription wakeups)
    {
        Task closing;

        lock (_subscriptionsGate)
        {
            if (!wakeups.Release())
                return Task.CompletedTask;

            closing = wakeups.Close();
        }

        return RemoveAfterCloseAsync(wakeups, closing);
    }

    async Task RemoveAfterCloseAsync(SharedWakeupSubscription wakeups, Task closing)
    {
        try
        {
            await closing.ConfigureAwait(false);
        }
        finally
        {
            lock (_subscriptionsGate)
            {
                if (_subscriptions.TryGetValue(wakeups.Pattern, out var current) &&
                    ReferenceEquals(current, wakeups))
                {
                    _ = _subscriptions.Remove(wakeups.Pattern);
                }
            }
        }
    }

    sealed class SharedWakeupSubscription : IAsyncDisposable
    {
        readonly CancellationTokenSource _lifetime = new();
        readonly FitzEventStore _owner;
        readonly Lock _gate = new();
        readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? _failure;
        bool _closed;
        Task? _closeTask;
        Task _run = Task.CompletedTask;
        long _version;
        TaskCompletionSource _changed = NewSignal();
        FitzWakeupSubscription<StreamCommitEvent>? _wakeups;

        public SharedWakeupSubscription(FitzEventStore owner, string pattern)
        {
            _owner = owner;
            Pattern = pattern;
        }

        public string Pattern { get; }

        public void Start()
        {
            lock (_gate)
                _run = RunAsync();
        }

        public Subscription Subscribe()
        {
            lock (_gate)
            {
                if (_closed || _failure is not null)
                    throw new InvalidOperationException("The shared Fitz subscription is no longer available.");

                return new Subscription(this, _version);
            }
        }

        public Task? Closed
        {
            get
            {
                lock (_gate)
                    return _closed ? _closeTask : null;
            }
        }

        public bool TrySubscribe(out Subscription subscription, out Task? closing)
        {
            lock (_gate)
            {
                if (!_closed && _failure is null)
                {
                    subscription = new Subscription(this, _version);
                    closing = null;
                    return true;
                }

                subscription = null!;
                closing = _closed ? _closeTask : null;
                return false;
            }
        }

        public bool Release()
        {
            lock (_gate)
            {
                if (_closed)
                    return false;

                _ = Interlocked.Decrement(ref _logicalSubscribers);

                if (Volatile.Read(ref _logicalSubscribers) != 0)
                    return false;

                _closed = true;
                _ = _changed.TrySetResult();
                return true;
            }
        }

        int _logicalSubscribers;

        public Task StartedAsync(CancellationToken ct) => _started.Task.WaitAsync(ct);

        public Task Close()
        {
            lock (_gate)
                return _closeTask ??= CloseAsync();
        }

        async Task CloseAsync()
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);

            FitzWakeupSubscription<StreamCommitEvent>? wakeups;
            lock (_gate)
                wakeups = _wakeups;

            if (wakeups is not null)
                await wakeups.DisposeAsync().ConfigureAwait(false);
            else
                await _run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        async Task RunAsync()
        {
            FitzWakeupSubscription<StreamCommitEvent>? wakeups = null;

            try
            {
                wakeups = await FitzWakeupSubscription<StreamCommitEvent>.SubscribeAsync(
                        async token => await _owner._streams.SubscribeAsync(Pattern, token).ConfigureAwait(false),
                        "The Fitz stream subscription ended without cancellation.", _lifetime.Token)
                    .ConfigureAwait(false);
                bool closed;

                lock (_gate)
                {
                    closed = _closed;

                    if (!closed)
                        _wakeups = wakeups;
                }

                if (closed)
                {
                    await wakeups.DisposeAsync().ConfigureAwait(false);
                    _ = _started.TrySetCanceled(_lifetime.Token);
                    return;
                }

                _ = _started.TrySetResult();

                while (true)
                {
                    try
                    {
                        await wakeups.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                    {
                        return;
                    }

                    Broadcast();
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                _ = _started.TrySetCanceled(_lifetime.Token);
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        public ValueTask DisposeAsync() => new(Close());

        void Broadcast()
        {
            TaskCompletionSource changed;

            lock (_gate)
            {
                if (_closed)
                    return;

                checked
                {
                    _version++;
                }

                changed = _changed;
                _changed = NewSignal();
            }

            _ = changed.TrySetResult();
        }

        void Fail(Exception exception)
        {
            TaskCompletionSource changed;

            lock (_gate)
            {
                if (_closed)
                    return;

                _failure = exception;
                changed = _changed;
            }

            _ = _started.TrySetException(exception);
            _ = changed.TrySetResult();
        }

        public void Wait(ref long version, out Task changed, out Exception? failure, out bool closed)
        {
            lock (_gate)
            {
                failure = _failure;
                closed = _closed;

                if (_closed)
                {
                    changed = _run;
                    return;
                }

                if (version < _version)
                {
                    version = _version;
                    changed = Task.CompletedTask;
                    return;
                }

                changed = _changed.Task;
            }
        }

        static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public sealed class Subscription : IDomainEventSubscription
        {
            readonly SharedWakeupSubscription _owner;
            readonly SemaphoreSlim _wait = new(1, 1);
            long _observedVersion;
            int _disposed;
            public Subscription(SharedWakeupSubscription owner, long observedVersion)
            {
                _owner = owner;
                _observedVersion = observedVersion;
                _ = Interlocked.Increment(ref _owner._logicalSubscribers);
            }

            public Task StartedAsync(CancellationToken ct) => _owner.StartedAsync(ct);

            public async ValueTask WaitAsync(CancellationToken ct = default)
            {
                await _wait.WaitAsync(ct).ConfigureAwait(false);

                try
                {
                    while (true)
                    {
                        _owner.Wait(ref _observedVersion, out var changed, out var failure, out var closed);

                        if (closed)
                        {
                            await changed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                            return;
                        }

                        if (ReferenceEquals(changed, Task.CompletedTask))
                            return;

                        if (failure is not null)
                            ExceptionDispatchInfo.Capture(failure).Throw();

                        await changed.WaitAsync(ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _ = _wait.Release();
                }
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return ValueTask.CompletedTask;

                return new ValueTask(_owner._owner.Release(_owner));
            }
        }
    }
}
