namespace Cntryl.Portia;

sealed class PassthroughDomainEventSerializer : IDomainEventSerializer
{
    public ReadOnlyMemory<byte> Serialize(DomainEvent ev) => ReadOnlyMemory<byte>.Empty;
    public DomainEvent Deserialize(ReadOnlyMemory<byte> data) => throw new NotSupportedException();
}
