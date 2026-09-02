namespace Cntryl.Portia;

/// <summary>
/// Reads ordered domain events from durable streams.
/// </summary>
public interface IDomainEventReader
{
    /// <summary>
    /// Reads committed events after a known aggregate version.
    /// </summary>
    /// <param name="stream">The aggregate stream identity.</param>
    /// <param name="afterVersion">The last aggregate version already known by the caller.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The next contiguous committed events in ascending aggregate-version order.</returns>
    IAsyncEnumerable<DomainEvent> ReadAsync(
        EventStreamAddress stream,
        ulong afterVersion = 0,
        CancellationToken ct = default);

    /// <summary>
    /// Reads events selected for a projector or reactor from a scope offset.
    /// </summary>
    /// <param name="pattern">The realm or area stream pattern.</param>
    /// <param name="fromOffset">The first inclusive realm or area offset to read.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>Events with their concrete source streams and checkpointable offsets.</returns>
    IAsyncEnumerable<DomainEventRecord> ReadAsync(
        EventStreamPattern pattern,
        ulong fromOffset = 0,
        CancellationToken ct = default);
}
