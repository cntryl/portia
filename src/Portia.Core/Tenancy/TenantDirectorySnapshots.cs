namespace Cntryl.Portia;

/// <summary>Creates lifecycle directories backed by durable roster snapshots.</summary>
public static class TenantDirectorySnapshots
{
    /// <summary>Creates a directory that resumes from a durable roster and lifecycle cursor.</summary>
    public static EventSourcedTenantDirectory<TStartEvent, TStopEvent> Create<TStartEvent, TStopEvent>(
        IDomainEventReader reader, EventStreamPattern pattern, Func<DomainEvent, TenantId> getTenantId,
        ITenantDirectorySnapshotStore snapshotStore, TimeSpan? pollInterval = null,
        TimeProvider? timeProvider = null, IDomainEventNotifier? notifier = null)
        where TStartEvent : DomainEvent
        where TStopEvent : DomainEvent
    {
        ArgumentNullException.ThrowIfNull(snapshotStore);
        return new EventSourcedTenantDirectory<TStartEvent, TStopEvent>(reader, pattern, getTenantId,
            pollInterval, timeProvider, notifier, snapshotStore);
    }
}
