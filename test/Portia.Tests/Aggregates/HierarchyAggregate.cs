namespace Cntryl.Portia;

sealed class HierarchyAggregate : Aggregate
{
    public HierarchyAggregate(Uuid id)
        : base(id, new EventStreamAddress("test", "aggregates", id.ToString()))
    {
        On<HierarchyBaseEvent>(_ => Handled = "base");
        On<HierarchyChildEvent>(ev => Handled = $"child:{ev.Amount}");
    }

    public string? Handled { get; private set; }

    public void RaiseChild(int amount) => RaiseEvent(new HierarchyChildEvent(amount));
}
