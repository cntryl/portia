namespace Cntryl.Portia;

/// <summary>
/// The event/metadata invariants <see cref="FitzEventStore" /> enforces on read and append —
/// kept separate from it since these rules change for an independent reason (what makes an
/// event valid) than stream I/O orchestration does.
/// </summary>
static class DomainEventInvariants
{
    /// <summary>
    /// Validates an event's metadata and that its ID hasn't already appeared in
    /// <paramref name="eventIds" /> — an append-time uniqueness check across the whole batch
    /// being appended.
    /// </summary>
    public static void ValidateEvent(DomainEvent ev, ulong expectedVersion, HashSet<Uuid> eventIds)
    {
        ValidateEventMetadata(ev, expectedVersion);

        if (!eventIds.Add(ev.Metadata.EventId))
            throw new InvalidOperationException($"Event ID '{ev.Metadata.EventId}' appears more than once.");
    }

    /// <summary>
    /// Validates an event's ID and aggregate version against what was expected.
    /// </summary>
    public static void ValidateEventMetadata(DomainEvent ev, ulong expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(ev);

        if (ev.Metadata.EventId == Uuid.Empty)
            throw new InvalidOperationException("An event ID cannot be empty.");

        if (ev.Metadata.AggregateVersion != expectedVersion)
        {
            throw new InvalidOperationException(
                $"Event aggregate version '{ev.Metadata.AggregateVersion}' does not match expected version '{expectedVersion}'.");
        }
    }
}
