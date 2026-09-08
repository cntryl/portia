namespace Cntryl.Portia;

/// <summary>
/// Verifies that generated event dispatch correctly favors the most specific event type
/// when an aggregate handles both a base event type and a type derived from it.
/// </summary>
public sealed class AggregateEventDispatcherOrderingTests
{
    /// <summary>
    /// Verifies that a handler for a derived event type is invoked in preference to a handler
    /// for its base event type, regardless of how the two type names sort alphabetically.
    /// </summary>
    [Fact]
    public void ShouldPreferDerivedEventHandlerWhenBaseHandlerAlsoRegistered()
    {
        var aggregate = new HierarchyAggregate(Uuid.CreateVersion4());

        aggregate.RaiseChild(7);

        Assert.Equal("child:7", aggregate.Handled);
    }
}

/// <summary>
/// Base event type. Named to sort alphabetically before its derived type below, which is what
/// makes an alphabetical (rather than specificity-based) dispatch ordering pick the wrong case.
/// </summary>
public abstract record HierarchyBaseEvent : DomainEvent;

/// <summary>
/// Event type derived from <see cref="HierarchyBaseEvent"/>.
/// </summary>
[Discriminator("test.hierarchy.child")]
public sealed record HierarchyChildEvent(int Amount) : HierarchyBaseEvent;

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
