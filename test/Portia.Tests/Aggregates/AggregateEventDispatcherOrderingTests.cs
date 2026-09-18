namespace Cntryl.Portia;

/// <summary>
///     Verifies that generated event dispatch correctly favors the most specific event type
///     when an aggregate handles both a base event type and a type derived from it.
/// </summary>
public sealed class AggregateEventDispatcherOrderingTests
{
    /// <summary>
    ///     Verifies that a handler for a derived event type is invoked in preference to a handler
    ///     for its base event type, regardless of how the two type names sort alphabetically.
    /// </summary>
    [Fact]
    public void ShouldPreferDerivedEventHandlerWhenBaseHandlerAlsoRegistered()
    {
        var aggregate = new HierarchyAggregate(Uuid.CreateVersion4());

        aggregate.RaiseChild(7);

        Assert.Equal("child:7", aggregate.Handled);
    }
}
