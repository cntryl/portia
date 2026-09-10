namespace Cntryl.Portia;

sealed class SystemDomainEventMetadataFactory : IDomainEventMetadataFactory
{
    SystemDomainEventMetadataFactory()
    {
    }

    public static SystemDomainEventMetadataFactory Instance { get; } = new();

    public DomainEventMetadata Create(Uuid aggregateId, ulong aggregateVersion) =>
        new(Uuid.CreateVersion4(), aggregateId, aggregateVersion, DateTimeOffset.UtcNow);
}
