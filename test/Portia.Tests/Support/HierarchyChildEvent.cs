namespace Cntryl.Portia;

/// <summary>
///     Event type derived from <see cref="HierarchyBaseEvent" />.
/// </summary>
[Discriminator("test.hierarchy.child")]
public sealed record HierarchyChildEvent(int Amount) : HierarchyBaseEvent;
