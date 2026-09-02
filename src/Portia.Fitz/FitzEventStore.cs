using System.Runtime.CompilerServices;
using System.Text;
using Cntryl.Fitz.Abstractions.Domains.Stream;

namespace Cntryl.Portia;

/// <summary>
/// Persists aggregate event histories in Fitz streams.
/// </summary>
public sealed class FitzEventStore : IEventStore
{
    const ulong ReadPageSize = 1024;

    readonly IStreamClient _streams;
    readonly IDomainEventSerializer _serializer;

    /// <summary>
    /// Creates a Fitz-backed event store.
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
    public async IAsyncEnumerable<DomainEvent> ReadAsync(
        EventStreamAddress stream,
        ulong afterVersion = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var route = stream.ToString();
        var startOffset = afterVersion;
        var nextVersion = checked(afterVersion + 1);
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
                var expectedOffset = nextVersion - 1;

                if (record.Offset != expectedOffset)
                {
                    throw new InvalidOperationException(
                        $"Fitz stream offset '{record.Offset}' does not match expected offset '{expectedOffset}'.");
                }

                var ev = _serializer.Deserialize(record.Body);
                ValidateEvent(ev, nextVersion, eventIds);
                yield return ev;
                nextVersion++;
            }

            if (!page.Cursor.HasMore)
                yield break;

            startOffset = checked(page.Cursor.LastResourceOffset + 1);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DomainEventRecord> ReadAsync(
        EventStreamPattern pattern,
        ulong fromOffset = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        EnsureSupported(pattern);
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

                if (!Matches(stream, pattern))
                    throw new InvalidOperationException($"Stream '{stream}' does not match pattern '{pattern}'.");

                var areaOffset = record.AreaOffset ?? throw new InvalidOperationException(
                    "A Portia Fitz record does not contain an area offset.");
                var realmOffset = record.RealmOffset ?? throw new InvalidOperationException(
                    "A Portia Fitz record does not contain a realm offset.");
                var scopeOffset = GetPatternOffset(pattern, record.Offset, areaOffset, realmOffset);

                if (scopeOffset != nextOffset)
                {
                    throw new InvalidOperationException(
                        $"Fitz pattern offset '{scopeOffset}' does not match expected offset '{nextOffset}'.");
                }

                var ev = _serializer.Deserialize(record.Body);
                ValidateEventMetadata(ev, checked(record.Offset + 1));
                yield return new DomainEventRecord(stream, ev, record.Offset, areaOffset, realmOffset);
                nextOffset++;
            }

            if (!page.Cursor.HasMore)
                yield break;

            startOffset = GetNextOffset(pattern, page.Cursor);
        }
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(
        EventStreamAddress stream,
        ulong expectedVersion,
        IReadOnlyList<DomainEvent> events,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(events);
        var eventIds = new HashSet<Uuid>();

        for (var index = 0; index < events.Count; index++)
            ValidateEvent(events[index], checked(expectedVersion + (ulong)index + 1), eventIds);

        if (events.Count == 0)
            return;

        var session = await _streams.BeginAsync(stream.ToString(), ct: ct).ConfigureAwait(false);
        var streamMetadata = Encoding.UTF8.GetBytes(stream.ToString());

        try
        {
            for (var index = 0; index < events.Count; index++)
            {
                var ev = events[index];
                var expectedOffset = checked(expectedVersion + (ulong)index);
                _ = await session.AppendAsync(
                    expectedOffset,
                    _serializer.Serialize(ev),
                    streamMetadata,
                    ct: ct).ConfigureAwait(false);
            }

            await session.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await RollbackAsync(session).ConfigureAwait(false);
            throw;
        }
    }

    static void ValidateEvent(DomainEvent ev, ulong expectedVersion, HashSet<Uuid> eventIds)
    {
        ValidateEventMetadata(ev, expectedVersion);

        if (!eventIds.Add(ev.Metadata.EventId))
            throw new InvalidOperationException($"Event ID '{ev.Metadata.EventId}' appears more than once.");
    }

    static void ValidateEventMetadata(DomainEvent ev, ulong expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(ev);

        if (ev.Metadata.EventId == Uuid.Empty)
            throw new InvalidOperationException("An event ID cannot be empty.");

        if (ev.Metadata.AggregateVersion != expectedVersion)
        {
            throw new InvalidOperationException(
                $"Event aggregate version '{ev.Metadata.AggregateVersion}' does not match expected version '{expectedVersion}'.");
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

    static bool Matches(EventStreamAddress stream, EventStreamPattern pattern) =>
        (pattern.Realm is null || stream.Realm == pattern.Realm)
        && (pattern.Area is null || stream.Area == pattern.Area)
        && (pattern.Resource is null || stream.Resource == pattern.Resource);

    static ulong GetPatternOffset(
        EventStreamPattern pattern,
        ulong resourceOffset,
        ulong areaOffset,
        ulong realmOffset) => pattern.Scope switch
        {
            EventStreamPatternScope.Resource => resourceOffset,
            EventStreamPatternScope.Area => areaOffset,
            EventStreamPatternScope.Realm => realmOffset,
            EventStreamPatternScope.Global => throw new NotSupportedException(
                "Published Fitz 0.1.0 does not expose global stream offsets."),
            _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
        };

    static ulong GetNextOffset(EventStreamPattern pattern, StreamReadCursor cursor) => pattern.Scope switch
    {
        EventStreamPatternScope.Resource => checked(cursor.LastResourceOffset + 1),
        EventStreamPatternScope.Area => checked((cursor.LastAreaOffset
            ?? throw new InvalidOperationException("Fitz did not return an area cursor.")) + 1),
        EventStreamPatternScope.Realm => checked((cursor.LastRealmOffset
            ?? throw new InvalidOperationException("Fitz did not return a realm cursor.")) + 1),
        EventStreamPatternScope.Global => throw new NotSupportedException(
            "Published Fitz 0.1.0 does not expose global stream offsets."),
        _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
    };

    static void EnsureSupported(EventStreamPattern pattern)
    {
        if (pattern.Scope == EventStreamPatternScope.Global)
            throw new NotSupportedException("Published Fitz 0.1.0 does not expose global stream offsets.");
    }

}
