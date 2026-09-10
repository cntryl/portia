namespace Cntryl.Portia;

sealed class DuplicateHandlerAggregate : Aggregate
{
    public DuplicateHandlerAggregate(Uuid id)
        : base(id, new EventStreamAddress("test", "aggregates", id.ToString()))
    {
        On<ValueChanged>(ev => Value = ev.Value);
        On<ValueChanged>(ev => Value = ev.Value * 2);
    }

    public int Value { get; private set; }
}
