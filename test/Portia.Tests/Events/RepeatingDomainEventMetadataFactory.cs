namespace Cntryl.Portia;

sealed class RepeatingDomainEventMetadataFactory(Uuid eventId) : IDomainEventMetadataFactory
{
    public DomainEventMetadata Create(Uuid aggregateId, ulong aggregateVersion) =>
        new(eventId, aggregateId, aggregateVersion, DateTimeOffset.UtcNow);
}
