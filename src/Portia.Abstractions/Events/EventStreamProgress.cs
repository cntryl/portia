using System.Text.Json.Serialization;

namespace Cntryl.Portia;

/// <summary>A source observation relative to one processor checkpoint; null measurements are unknown.</summary>
/// <remarks>
///     Counts describe reader-visible source records. A known gap or withheld history leaves the exact
///     count unknown. Caught-up status does not establish intact historical retention or business completion.
/// </remarks>
/// <param name="Position">The supplied committed processor position, unchanged.</param>
/// <param name="SourcePosition">The observed source's next position, or null when it cannot be established.</param>
/// <param name="PendingEvents">
///     The exact number of unread source records, including audits, or null when unavailable. This is
///     not a count of matching handler invocations, and does not count missing or withheld records as events.
/// </param>
/// <param name="IsCaughtUp">
///     Whether no reader-visible work remains after a validated checkpoint, or null when unknown.
///     Known skipped history prevents a caught-up result even when no readable record was returned.
/// </param>
/// <param name="ObservedAt">When the source observation completed.</param>
/// <param name="PendingEventsLowerBound">
///     A proven minimum unread source-record count when the exact count is unavailable, or the exact
///     count when known. A bounded provider probe may leave both the source position and exact count unknown.
/// </param>
public sealed record EventStreamProgress(
    [property: JsonConverter(typeof(EventCursorJsonConverter))] EventCursor Position,
    [property: JsonConverter(typeof(EventCursorJsonConverter))] EventCursor? SourcePosition,
    ulong? PendingEvents, bool? IsCaughtUp, DateTimeOffset ObservedAt, ulong? PendingEventsLowerBound = null);
