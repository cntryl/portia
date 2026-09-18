namespace Cntryl.Portia;

/// <summary>
///     Appends ordered domain events to durable streams.
/// </summary>
public interface IDomainEventWriter
{
    /// <summary>
    ///     Appends events or audits when the stream is at the expected physical position.
    /// </summary>
    /// <param name="stream">The aggregate stream identity.</param>
    /// <param name="expectedStreamPosition">
    ///     The next physical resource offset before append; includes previously committed
    ///     audits.
    /// </param>
    /// <param name="events">The homogeneous batch of state-changing events or audits to append.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task representing the append operation.</returns>
    ValueTask AppendAsync(
        EventStreamAddress stream,
        ulong expectedStreamPosition,
        IReadOnlyList<DomainEvent> events,
        CancellationToken ct = default);
}
