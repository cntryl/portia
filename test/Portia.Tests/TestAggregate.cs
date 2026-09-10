namespace Cntryl.Portia;

sealed class TestAggregate : Aggregate
{
    public TestAggregate(Uuid id, IDomainEventMetadataFactory? metadataFactory = null)
        : base(id, new EventStreamAddress("test", "aggregates", id.ToString()), metadataFactory)
    {
        On<ValueChanged>(ev => Value = ev.Value);
        On<ValueIncremented>(ev => Value += ev.Amount);
    }

    public int Value { get; private set; }

    public void ChangeValue(int value) => RaiseEvent(new ValueChanged(value));

    public void Audit(string reason) => AuditEvent(new ValueAudited(reason));
}
