namespace Cntryl.Portia;

/// <summary>
/// Appends ordered domain events to durable streams.
/// </summary>
public interface IDomainEventWriter
{
    /// <summary>
    /// Appends state-changing events when the stream is at the expected version.
    /// </summary>
    /// <param name="stream">The aggregate stream identity.</param>
    /// <param name="expectedVersion">The stream version before the events are appended.</param>
    /// <param name="events">The contiguous state-changing events to append.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the append operation.</returns>
    ValueTask AppendAsync(
        EventStreamAddress stream,
        ulong expectedVersion,
        IReadOnlyList<DomainEvent> events,
        CancellationToken ct = default);
}
