namespace Cntryl.Portia;

/// <summary>Convenience entry points for starting event reads.</summary>
public static class DomainEventReaderExtensions
{
    /// <summary>Reads a concrete stream from its beginning.</summary>
    public static IAsyncEnumerable<DomainEventRecord> ReadAsync(
        this IDomainEventReader reader,
        EventStreamAddress stream)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.ReadAsync(stream, 0, default);
    }

    /// <summary>Reads a concrete stream from its beginning.</summary>
    public static IAsyncEnumerable<DomainEventRecord> ReadAsync(
        this IDomainEventReader reader,
        EventStreamAddress stream,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.ReadAsync(stream, 0, ct);
    }

    /// <summary>Reads a concrete stream from a resource offset.</summary>
    public static IAsyncEnumerable<DomainEventRecord> ReadAsync(
        this IDomainEventReader reader,
        EventStreamAddress stream,
        ulong fromOffset)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.ReadAsync(stream, fromOffset, default);
    }

    /// <summary>Reads a stream pattern from its beginning.</summary>
    public static IAsyncEnumerable<DomainEventRecord> ReadAsync(
        this IDomainEventReader reader,
        EventStreamPattern pattern)
        =>
            reader.ReadAsync(pattern, default);

    /// <summary>Reads a stream pattern from its beginning.</summary>
    public static IAsyncEnumerable<DomainEventRecord> ReadAsync(
        this IDomainEventReader reader,
        EventStreamPattern pattern,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.ReadAsync(pattern, EventCursor.Start, ct);
    }
}
