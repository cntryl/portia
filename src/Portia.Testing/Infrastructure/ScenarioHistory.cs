namespace Cntryl.Portia.Testing;

// The events a component scenario delivers. Bare events belong to one scenario aggregate and are numbered in
// order; seeded events keep their metadata. Delivery always replays them into streams the component consumes.
sealed class ScenarioHistory
{
    readonly TenantId _tenant;
    readonly Uuid _aggregateId = Uuid.CreateVersion4();
    readonly List<DomainEvent> _events = [];
    ulong _version;

    public ScenarioHistory(TenantId tenant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant.Value, nameof(tenant));
        _tenant = tenant;
    }

    public void Add(DomainEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var ev in events)
        {
            ArgumentNullException.ThrowIfNull(ev);
            if (ev.AttachedMetadata is null)
                _ = DomainEventSeed.Attach(ev, _aggregateId, ++_version);
            _events.Add(ev);
        }
    }

    // A tenant template only runs once bound, which hosting does per tenant; tests run as one scenario tenant.
    public WorkloadIdentity Workload(string name) => new(name, _tenant);

    public async ValueTask<InMemoryEventStore> ToStoreAsync(EventStreamPattern pattern, CancellationToken ct)
    {
        var store = new InMemoryEventStore();
        var positions = new Dictionary<EventStreamAddress, ulong>();
        foreach (var ev in _events)
        {
            var stream = new EventStreamAddress(pattern.Realm, pattern.Area ?? "scenario",
                pattern.Resource ?? ev.Metadata.AggregateId.ToString());
            var position = positions.GetValueOrDefault(stream);
            await store.AppendAsync(stream, position, [ev], ct).ConfigureAwait(false);
            positions[stream] = position + 1;
        }

        return store;
    }
}
