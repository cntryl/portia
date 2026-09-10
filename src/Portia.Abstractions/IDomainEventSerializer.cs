namespace Cntryl.Portia;

/// <summary>
///     Converts concrete domain events to and from their durable representation.
/// </summary>
public interface IDomainEventSerializer
{
    /// <summary>
    ///     Serializes a concrete domain event.
    /// </summary>
    /// <param name="ev">The event to serialize.</param>
    /// <returns>The durable event representation.</returns>
    ReadOnlyMemory<byte> Serialize(DomainEvent ev);

    /// <summary>
    ///     Deserializes a concrete domain event.
    /// </summary>
    /// <param name="data">The durable event representation.</param>
    /// <returns>The deserialized concrete event.</returns>
    DomainEvent Deserialize(ReadOnlyMemory<byte> data);
}
