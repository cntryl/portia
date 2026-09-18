namespace Cntryl.Portia.Consumer;

public sealed class Account : Aggregate
{
    public Account(Uuid id, IDomainEventMetadataFactory? metadataFactory = null)
        : base(id, ConsumerStreams.AccountStream(id), metadataFactory)
    {
        On<Deposited>(ev => Balance += ev.Amount);
    }

    public int Balance { get; private set; }

    public void Deposit(int amount) => RaiseEvent(new Deposited(amount));

    public void Raise(DomainEvent ev) => RaiseEvent(ev);

    public void Audit(DomainEvent ev) => AuditEvent(ev);
}
