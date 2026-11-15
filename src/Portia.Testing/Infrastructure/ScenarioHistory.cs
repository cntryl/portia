namespace Cntryl.Portia.Testing;

// The events a component scenario delivers. Bare events belong to one scenario aggregate per stream and are
// numbered in order; seeded events keep their metadata. Delivery always replays them into streams the component
// consumes: the realm and area come from its pattern, the resource from the test or else the aggregate ID.
sealed class ScenarioHistory
{
    readonly TenantId _tenant;
    readonly Dictionary<string, (Uuid AggregateId, ulong Version)> _aggregates = [];
    readonly List<(DomainEvent Event, string? Resource)> _events = [];

    public ScenarioHistory(TenantId tenant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant.Value, nameof(tenant));
        _tenant = tenant;
    }

    public void Add(DomainEvent[] events) => AddCore(null, events);

    public void Add(string resource, DomainEvent[] events)
    {
        // The address applies the segment rules; only the resource is the caller's.
        _ = new EventStreamAddress("scenario", "scenario", resource);
        AddCore(resource, events);
    }

    void AddCore(string? resource, DomainEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var ev in events)
        {
            ArgumentNullException.ThrowIfNull(ev);
            if (ev.AttachedMetadata is null)
            {
                // The empty key is the scenario's default aggregate; a resource segment is never empty.
                var key = resource ?? "";
                if (!_aggregates.TryGetValue(key, out var aggregate))
                    aggregate = (Uuid.CreateVersion4(), 0);
                aggregate.Version++;
                _aggregates[key] = aggregate;
                _ = DomainEventSeed.Attach(ev, aggregate.AggregateId, aggregate.Version);
            }

            _events.Add((ev, resource));
        }
    }

    // A tenant template only runs once bound, which hosting does per tenant; tests run as one scenario tenant.
    public WorkloadIdentity Workload(string name) => new(name, _tenant);

    public async ValueTask<InMemoryEventStore> ToStoreAsync(EventStreamPattern pattern, CancellationToken ct)
    {
        var store = new InMemoryEventStore();
        var positions = new Dictionary<EventStreamAddress, ulong>();
        foreach (var (ev, resource) in _events)
        {
            if (resource is not null && pattern.Resource is not null
                && !string.Equals(resource, pattern.Resource, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Events were given on stream resource '{resource}', which the pattern '{pattern}' never delivers.");
            }

            var stream = new EventStreamAddress(pattern.Realm, pattern.Area ?? "scenario",
                resource ?? pattern.Resource ?? ev.Metadata.AggregateId.ToString());
            var position = positions.GetValueOrDefault(stream);
            await store.AppendAsync(stream, position, [ev], ct).ConfigureAwait(false);
            positions[stream] = position + 1;
        }

        return store;
    }
}
