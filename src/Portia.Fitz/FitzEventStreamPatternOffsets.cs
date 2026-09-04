using Cntryl.Fitz.Abstractions.Domains.Stream;

namespace Cntryl.Portia;

/// <summary>
/// The pattern-scope offset arithmetic and stream/pattern matching <see cref="FitzEventStore" />
/// needs, kept separate from it so that class stays responsible only for orchestrating reads and
/// appends against <see cref="IStreamClient" /> — not for scope semantics, which change for an
/// independent reason (Fitz adding a new <see cref="EventStreamPatternScope" />, for example).
/// </summary>
static class FitzEventStreamPatternOffsets
{
    /// <summary>
    /// Returns whether <paramref name="stream" /> falls under <paramref name="pattern" /> — every
    /// realm and every non-null area or resource segment equal the stream's corresponding segment.
    /// </summary>
    public static bool Matches(EventStreamAddress stream, EventStreamPattern pattern) =>
        stream.Realm == pattern.Realm
        && (pattern.Area is null || stream.Area == pattern.Area)
        && (pattern.Resource is null || stream.Resource == pattern.Resource);

    /// <summary>
    /// Returns the offset that corresponds to <paramref name="pattern" />'s scope out of a
    /// record's resource/area/realm offsets.
    /// </summary>
    public static ulong GetPatternOffset(
        EventStreamPattern pattern,
        ulong resourceOffset,
        ulong? areaOffset,
        ulong? realmOffset) => pattern.Scope switch
        {
            EventStreamPatternScope.Resource => resourceOffset,
            EventStreamPatternScope.Area => areaOffset ?? throw new InvalidOperationException("Fitz did not return an area offset."),
            EventStreamPatternScope.Realm => realmOffset ?? throw new InvalidOperationException("Fitz did not return a realm offset."),
            _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
        };

    /// <summary>
    /// Returns the next offset to resume reading from out of a page cursor, for whichever scope
    /// <paramref name="pattern" /> selects.
    /// </summary>
    public static ulong GetNextOffset(EventStreamPattern pattern, StreamReadCursor cursor) => pattern.Scope switch
    {
        EventStreamPatternScope.Resource => checked(cursor.LastResourceOffset + 1),
        EventStreamPatternScope.Area => checked((cursor.LastAreaOffset
            ?? throw new InvalidOperationException("Fitz did not return an area cursor.")) + 1),
        EventStreamPatternScope.Realm => checked((cursor.LastRealmOffset
            ?? throw new InvalidOperationException("Fitz did not return a realm cursor.")) + 1),
        _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
    };
}
