namespace Cntryl.Portia;

/// <summary>Observes source progress relative to a processor's authoritative committed checkpoint.</summary>
/// <remarks>
///     This optional reader capability interprets its own opaque cursors. Observations do not advance
///     checkpoints or establish business outcomes, and may be followed immediately by new appends.
/// </remarks>
public interface IDomainEventProgressReader
{
    /// <summary>Reports the unread source records following a committed position in one bound pattern.</summary>
    /// <param name="pattern">The exact bound source pattern used by the processor.</param>
    /// <param name="position">The committed cursor issued by this reader for that pattern.</param>
    /// <param name="ct">Cancels the observation.</param>
    /// <returns>A sampled source position and backlog, preserving unknown values when unavailable.</returns>
    ValueTask<EventStreamProgress> GetProgressAsync(EventStreamPattern pattern, EventCursor position,
        CancellationToken ct = default);
}
