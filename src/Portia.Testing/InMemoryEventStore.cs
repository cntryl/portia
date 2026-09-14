using System.Globalization;

namespace Cntryl.Portia.Testing;

/// <summary>
///     Stores ordered aggregate event histories in memory for tests.
/// </summary>
public sealed class InMemoryEventStore : IEventStore
{
    readonly Dictionary<(string Realm, string Area), ulong> _areaOffsets = [];
    readonly Lock _gate = new();
    readonly Dictionary<string, ulong> _realmOffsets = [];

    readonly Dictionary<(EventStreamAddress Stream, ulong ResourceOffset), (ulong Area, ulong Realm)>
        _scopeOffsets = [];

    readonly Dictionary<EventStreamAddress, List<DomainEventRecord>> _streams = [];

    /// <inheritdoc />
    public IAsyncEnumerable<DomainEventRecord> ReadAsync(
        EventStreamAddress stream,
        ulong fromOffset,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ct.ThrowIfCancellationRequested();
        DomainEventRecord[] readBuffer;

        lock (_gate)
        {
            var records = _streams.TryGetValue(stream, out var committedRecords)
                ? committedRecords
                : [];

            readBuffer = fromOffset <= (ulong)records.Count
                ? [.. records.Skip((int)fromOffset)]
                : throw new InvalidOperationException(
                    $"Offset '{fromOffset}' is beyond the end of aggregate stream '{stream}'.");
        }

        return new BufferedAsyncEnumerable<DomainEventRecord>(readBuffer, ct);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<DomainEventRecord> ReadAsync(
        EventStreamPattern pattern,
        EventCursor cursor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ct.ThrowIfCancellationRequested();
        DomainEventRecord[] readBuffer;

        lock (_gate)
        {
            var fromOffset = Decode(cursor);
            readBuffer =
            [
                .. _streams
                    .Where(pair => Matches(pair.Key, pattern))
                    .SelectMany(pair => pair.Value)
                    .Where(record => GetPatternOffset(record, pattern) >= fromOffset)
                    .OrderBy(record => GetPatternOffset(record, pattern))
                    .Select(record => record with
                    {
                        NextCursor = Encode(checked(GetPatternOffset(record, pattern) + 1))
                    })
            ];
        }

        return new BufferedAsyncEnumerable<DomainEventRecord>(readBuffer, ct);
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(
        EventStreamAddress stream,
        ulong expectedStreamPosition,
        IReadOnlyList<DomainEvent> events,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(events);
        ct.ThrowIfCancellationRequested();

        if (events.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        DomainEventValidation.ValidateBatch(events);
        lock (_gate)
        {
            if (!_streams.TryGetValue(stream, out var committedRecords))
            {
                committedRecords = [];
            }

            if ((ulong)committedRecords.Count != expectedStreamPosition)
            {
                throw new EventStreamConcurrencyException(
                    $"Aggregate stream '{stream}' is at position '{committedRecords.Count}', not expected position '{expectedStreamPosition}'.");
            }

            var eventIds = committedRecords.Select(record => record.Event.Metadata.EventId).ToHashSet();

            for (var index = 0; index < events.Count; index++)
            {
                var ev = events[index];
                DomainEventValidation.Validate(ev);

                if (!eventIds.Add(ev.Metadata.EventId))
                {
                    throw new InvalidOperationException($"Event ID '{ev.Metadata.EventId}' has already been appended.");
                }
            }

            var areaKey = (stream.Realm, stream.Area);
            var areaOffset = _areaOffsets.GetValueOrDefault(areaKey);
            var realmOffset = _realmOffsets.GetValueOrDefault(stream.Realm);

            // Finish validation and checked arithmetic before making any part of the batch visible.
            _ = checked(expectedStreamPosition + (ulong)events.Count);
            var finalAreaOffset = checked(areaOffset + (ulong)events.Count);
            var finalRealmOffset = checked(realmOffset + (ulong)events.Count);
            _streams[stream] = committedRecords;

            for (var index = 0; index < events.Count; index++)
            {
                var resourceOffset = checked(expectedStreamPosition + (ulong)index);
                var eventAreaOffset = checked(areaOffset + (ulong)index);
                var eventRealmOffset = checked(realmOffset + (ulong)index);
                _scopeOffsets.Add((stream, resourceOffset), (eventAreaOffset, eventRealmOffset));
                committedRecords.Add(new DomainEventRecord(
                    stream,
                    events[index],
                    resourceOffset,
                    Encode(checked(resourceOffset + 1))));
            }

            _areaOffsets[areaKey] = finalAreaOffset;
            _realmOffsets[stream.Realm] = finalRealmOffset;
        }

        return ValueTask.CompletedTask;
    }

    static bool Matches(EventStreamAddress stream, EventStreamPattern pattern) =>
        (pattern.Realm is null || stream.Realm == pattern.Realm)
        && (pattern.Area is null || stream.Area == pattern.Area)
        && (pattern.Resource is null || stream.Resource == pattern.Resource);

    ulong GetPatternOffset(DomainEventRecord record, EventStreamPattern pattern) => pattern.Scope switch
    {
        EventStreamPatternScope.Resource => record.ResourceOffset,
        EventStreamPatternScope.Area => _scopeOffsets[(record.Stream, record.ResourceOffset)].Area,
        EventStreamPatternScope.Realm => _scopeOffsets[(record.Stream, record.ResourceOffset)].Realm,
        _ => throw new ArgumentOutOfRangeException(nameof(pattern))
    };

    static EventCursor Encode(ulong offset) => new(offset.ToString(CultureInfo.InvariantCulture));

    static ulong Decode(EventCursor cursor) => cursor == EventCursor.Start
        ? 0
        : ulong.TryParse(cursor.Value, NumberStyles.None,
            CultureInfo.InvariantCulture, out var offset)
            ? offset
            : throw new ArgumentException("The event cursor was not issued by this event store.", nameof(cursor));

    sealed class BufferedAsyncEnumerable<T>(T[] items, CancellationToken readCt)
        : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
            => new BufferedAsyncEnumerator<T>(items, readCt, ct);
    }

    sealed class BufferedAsyncEnumerator<T> : IAsyncEnumerator<T>
    {
        readonly CancellationToken _ct;
        readonly T[] _items;
        readonly CancellationTokenSource? _linkedCts;
        int _index = -1;

        public BufferedAsyncEnumerator(
            T[] items,
            CancellationToken readCt,
            CancellationToken enumerationCt)
        {
            _items = items;
            _linkedCts = readCt.CanBeCanceled && enumerationCt.CanBeCanceled && readCt != enumerationCt
                ? CancellationTokenSource.CreateLinkedTokenSource(readCt, enumerationCt)
                : null;
            _ct = _linkedCts?.Token ?? (readCt.CanBeCanceled ? readCt : enumerationCt);
        }

        public T Current => _items[_index];

        public ValueTask<bool> MoveNextAsync()
        {
            _ct.ThrowIfCancellationRequested();
            _index++;
            return ValueTask.FromResult(_index < _items.Length);
        }

        public ValueTask DisposeAsync()
        {
            _linkedCts?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
