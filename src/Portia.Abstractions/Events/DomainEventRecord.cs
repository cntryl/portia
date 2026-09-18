namespace Cntryl.Portia;

/// <summary>
///     Describes a domain event read from a concrete stream, with the offsets a projector or reactor checkpoints against.
/// </summary>
/// <param name="Stream">The concrete source stream.</param>
/// <param name="Event">The domain event.</param>
/// <param name="ResourceOffset">The zero-based offset within the resource stream.</param>
/// <param name="NextCursor">The opaque cursor with which the current read can be resumed.</param>
public sealed record DomainEventRecord(
    EventStreamAddress Stream,
    DomainEvent Event,
    ulong ResourceOffset,
    EventCursor NextCursor);
