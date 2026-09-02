namespace Cntryl.Portia;

/// <summary>
/// Reads and writes ordered domain-event streams.
/// </summary>
public interface IEventStore : IDomainEventReader, IDomainEventWriter
{
}
