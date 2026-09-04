namespace Cntryl.Portia;

/// <summary>
/// Creates metadata for newly raised aggregate events. Applications can supply an implementation
/// that uses their clock and identity source and propagates correlation or causation context.
/// </summary>
public interface IDomainEventMetadataFactory
{
    /// <summary>
    /// Creates metadata for a new event at the supplied aggregate identity and version.
    /// </summary>
    /// <param name="aggregateId">The aggregate raising the event.</param>
    /// <param name="aggregateVersion">The version assigned to the event.</param>
    /// <returns>The complete event metadata.</returns>
    DomainEventMetadata Create(Uuid aggregateId, ulong aggregateVersion);
}

sealed class SystemDomainEventMetadataFactory : IDomainEventMetadataFactory
{
    public static SystemDomainEventMetadataFactory Instance { get; } = new();

    SystemDomainEventMetadataFactory()
    {
    }

    public DomainEventMetadata Create(Uuid aggregateId, ulong aggregateVersion) =>
        new(Uuid.CreateVersion7(), aggregateId, aggregateVersion, DateTimeOffset.UtcNow);
}
