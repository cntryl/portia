namespace Cntryl.Portia;

static class DomainEventValidation
{
    public static void Validate(DomainEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var metadata = ev.Metadata;
        if (metadata.EventId == Uuid.Empty || metadata.AggregateId == Uuid.Empty)
            throw new InvalidOperationException("Event and aggregate identities cannot be empty.");
        if (metadata.OccurredOn.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Event occurrence time must be UTC.");
        if (!metadata.IsAudit && metadata.AggregateVersion == 0)
            throw new InvalidOperationException("A state-changing event must advance the aggregate version.");
    }

    public static void ValidateBatch(IReadOnlyList<DomainEvent> events)
    {
        var eventIds = new HashSet<Uuid>();
        for (var index = 0; index < events.Count; index++)
        {
            var ev = events[index];
            Validate(ev);
            var first = events[0].Metadata;
            var expectedVersion = checked(first.AggregateVersion + (first.IsAudit ? 0 : (ulong)index));
            if (ev.Metadata.AggregateId != first.AggregateId || ev.Metadata.IsAudit != first.IsAudit
                || ev.Metadata.AggregateVersion != expectedVersion)
            {
                throw new InvalidOperationException("A batch must contain one aggregate's raised events in order or audits at one state version.");
            }

            if (!eventIds.Add(ev.Metadata.EventId))
                throw new InvalidOperationException($"Event ID '{ev.Metadata.EventId}' appears more than once.");
        }
    }
}
