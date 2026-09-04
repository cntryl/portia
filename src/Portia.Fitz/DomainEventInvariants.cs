namespace Cntryl.Portia;

static class DomainEventInvariants
{
    public static void ValidateEvent(DomainEvent ev, HashSet<Uuid> eventIds)
    {
        DomainEventValidation.Validate(ev);
        if (!eventIds.Add(ev.Metadata.EventId))
            throw new InvalidOperationException($"Event ID '{ev.Metadata.EventId}' appears more than once.");
    }
}
