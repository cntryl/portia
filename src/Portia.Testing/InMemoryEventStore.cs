namespace Cntryl.Portia.Testing;

/// <summary>
/// Stores ordered aggregate event histories in memory for tests.
/// </summary>
public sealed class InMemoryEventStore : IEventStore
{
    readonly Lock _gate = new();
    readonly Dictionary<EventStreamAddress, List<DomainEventRecord>> _streams = [];
    readonly Dictionary<(string Realm, string Area), ulong> _areaOffsets = [];
    readonly Dictionary<string, ulong> _realmOffsets = [];

    /// <inheritdoc />
    public IAsyncEnumerable<DomainEventRecord> ReadAsync(
        EventStreamAddress stream,
        ulong fromOffset = 0,
        CancellationToken ct = default)
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
        ulong fromOffset = 0,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ct.ThrowIfCancellationRequested();
        DomainEventRecord[] readBuffer;

        lock (_gate)
        {
            readBuffer = [.. _streams
                .Where(pair => Matches(pair.Key, pattern))
                .SelectMany(pair => pair.Value)
                .Where(record => GetPatternOffset(record, pattern) >= fromOffset)
                .OrderBy(record => GetPatternOffset(record, pattern))];
        }

        return new BufferedAsyncEnumerable<DomainEventRecord>(readBuffer, ct);
    }

    sealed class BufferedAsyncEnumerable<T>(T[] items, CancellationToken readCt)
        : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
            => new BufferedAsyncEnumerator<T>(items, readCt, ct);
    }

    sealed class BufferedAsyncEnumerator<T> : IAsyncEnumerator<T>
    {
        readonly T[] _items;
        readonly CancellationTokenSource? _linkedCts;
        readonly CancellationToken _ct;
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
            return ValueTask.CompletedTask;

        DomainEventValidation.ValidateBatch(events);
        lock (_gate)
        {
            if (!_streams.TryGetValue(stream, out var committedRecords))
            {
                committedRecords = [];
                _streams.Add(stream, committedRecords);
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
                    throw new InvalidOperationException($"Event ID '{ev.Metadata.EventId}' has already been appended.");
            }

            var areaKey = (stream.Realm, stream.Area);
            var areaOffset = _areaOffsets.GetValueOrDefault(areaKey);
            var realmOffset = _realmOffsets.GetValueOrDefault(stream.Realm);

            for (var index = 0; index < events.Count; index++)
            {
                committedRecords.Add(new DomainEventRecord(
                    stream,
                    events[index],
                    checked(expectedStreamPosition + (ulong)index),
                    checked(areaOffset + (ulong)index),
                    checked(realmOffset + (ulong)index)));
            }

            _areaOffsets[areaKey] = checked(areaOffset + (ulong)events.Count);
            _realmOffsets[stream.Realm] = checked(realmOffset + (ulong)events.Count);
        }

        return ValueTask.CompletedTask;
    }

    static bool Matches(EventStreamAddress stream, EventStreamPattern pattern) =>
        (pattern.Realm is null || stream.Realm == pattern.Realm)
        && (pattern.Area is null || stream.Area == pattern.Area)
        && (pattern.Resource is null || stream.Resource == pattern.Resource);

    static ulong GetPatternOffset(DomainEventRecord record, EventStreamPattern pattern) => pattern.Scope switch
    {
        EventStreamPatternScope.Resource => record.ResourceOffset,
        EventStreamPatternScope.Area => record.AreaOffset ?? throw new InvalidOperationException("Missing area offset."),
        EventStreamPatternScope.Realm => record.RealmOffset ?? throw new InvalidOperationException("Missing realm offset."),
        _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
    };
}
