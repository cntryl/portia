namespace Cntryl.Portia;

/// <summary>
///     Base event type. Named to sort alphabetically before its derived type below, which is what
///     makes an alphabetical (rather than specificity-based) dispatch ordering pick the wrong case.
/// </summary>
public abstract record HierarchyBaseEvent : DomainEvent;
