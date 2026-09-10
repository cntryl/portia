namespace Cntryl.Portia;

/// <summary>
///     Reads ordered domain events from durable streams.
/// </summary>
public interface IDomainEventReader
{
    /// <summary>
    ///     Reads committed records from an inclusive physical stream offset, including audits.
    /// </summary>
    /// <param name="stream">The aggregate stream identity.</param>
    /// <param name="fromOffset">The first inclusive physical resource offset to read.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The next contiguous committed records in ascending resource-offset order.</returns>
    IAsyncEnumerable<DomainEventRecord> ReadAsync(
        EventStreamAddress stream,
        ulong fromOffset = 0,
        CancellationToken ct = default);

    /// <summary>
    ///     Reads events selected for a projector or reactor from a scope offset.
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
