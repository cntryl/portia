namespace Cntryl.Portia.Testing;

/// <summary>Builds identified events for business tests and event-store seeding.</summary>
public static class DomainEventSeed
{
    /// <summary>Attaches metadata once to an event being seeded.</summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="ev">The event payload.</param>
    /// <param name="aggregateId">The aggregate identity.</param>
    /// <param name="aggregateVersion">The aggregate state version.</param>
    /// <param name="isAudit">Whether the record is an audit.</param>
    /// <param name="eventId">An explicit event identity, or a generated UUID.</param>
    /// <param name="occurredOn">An explicit UTC timestamp, or the current UTC time.</param>
    /// <returns>The identified event.</returns>
    public static TEvent Attach<TEvent>(TEvent ev, Uuid aggregateId, ulong aggregateVersion,
        bool isAudit = false, Uuid? eventId = null, DateTimeOffset? occurredOn = null)
        where TEvent : DomainEvent
    {
        ArgumentNullException.ThrowIfNull(ev);
        ev.AttachMetadata(new DomainEventMetadata(eventId ?? Uuid.CreateVersion4(), aggregateId,
            aggregateVersion, occurredOn ?? DateTimeOffset.UtcNow, IsAudit: isAudit));
        return ev;
    }
}
