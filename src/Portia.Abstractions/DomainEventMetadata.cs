namespace Cntryl.Portia;

/// <summary>
/// Describes the event-sourcing identity and causal context of a domain event.
/// </summary>
/// <param name="EventId">The globally unique identity of the event.</param>
/// <param name="AggregateId">The identity of the aggregate associated with the event.</param>
/// <param name="AggregateVersion">The aggregate version associated with the event.</param>
/// <param name="OccurredOn">The UTC time at which the event occurred.</param>
/// <param name="CorrelationId">The optional identity shared by a correlated operation.</param>
/// <param name="CausationId">The optional identity of the message that caused the event.</param>
public sealed record DomainEventMetadata(
    Uuid EventId,
    Uuid AggregateId,
    ulong AggregateVersion,
    DateTimeOffset OccurredOn,
    Uuid? CorrelationId = null,
    Uuid? CausationId = null);
