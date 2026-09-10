namespace Cntryl.Portia;

sealed class FixedDomainEventMetadataFactory(
    Uuid eventId,
    DateTimeOffset occurredOn,
    Uuid correlationId,
    Uuid causationId) : IDomainEventMetadataFactory
{
    public DomainEventMetadata Create(Uuid aggregateId, ulong aggregateVersion) =>
        new(eventId, aggregateId, aggregateVersion, occurredOn, correlationId, causationId);
}
