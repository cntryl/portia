namespace Cntryl.Portia;

/// <summary>
/// Describes a domain event read from a concrete stream with checkpointable Fitz offsets.
/// </summary>
/// <param name="Stream">The concrete source stream.</param>
/// <param name="Ev">The domain event.</param>
/// <param name="ResourceOffset">The zero-based offset within the resource stream.</param>
/// <param name="AreaOffset">The zero-based offset within the area.</param>
/// <param name="RealmOffset">The zero-based offset within the realm.</param>
/// <param name="GlobalOffset">The zero-based global offset when supplied by the event source.</param>
public sealed record DomainEventRecord(
    EventStreamAddress Stream,
    DomainEvent Ev,
    ulong ResourceOffset,
    ulong AreaOffset,
    ulong RealmOffset,
    ulong? GlobalOffset = null);
