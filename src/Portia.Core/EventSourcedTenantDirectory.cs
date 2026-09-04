namespace Cntryl.Portia;

/// <summary>
/// The default, low-boilerplate <see cref="ITenantDirectory" />: a tenant becomes active when a
/// <typeparamref name="TStartEvent" /> is committed, and stops being active when a
/// <typeparamref name="TStopEvent" /> is committed for the same tenant. Reads a single event
/// stream pattern — typically a fixed, non-tenant-scoped one (e.g. a realm/area dedicated to
/// tenant lifecycle) — the same way a <see cref="Reactor" /> reads its own pattern.
/// </summary>
/// <typeparam name="TStartEvent">The event that marks a tenant active.</typeparam>
/// <typeparam name="TStopEvent">The event that marks a tenant no longer active.</typeparam>
/// <param name="reader">The domain-event reader.</param>
/// <param name="pattern">The event stream pattern tenant lifecycle events are read from.</param>
/// <param name="getTenantId">Extracts the tenant id from a committed
/// <typeparamref name="TStartEvent" /> or <typeparamref name="TStopEvent" />.</param>
/// <param name="pollInterval">How often to check for new lifecycle events once caught up. A
/// <see langword="null" /> or non-positive value polls as fast as possible — fine for tests,
/// usually too aggressive for a real event store.</param>
public sealed class EventSourcedTenantDirectory<TStartEvent, TStopEvent>(
    IDomainEventReader reader,
    EventStreamPattern pattern,
    Func<DomainEvent, TenantId> getTenantId,
    TimeSpan? pollInterval = null)
    : ITenantDirectory
    where TStartEvent : DomainEvent
    where TStopEvent : DomainEvent
{
    readonly IDomainEventReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    readonly EventStreamPattern _pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
    readonly Func<DomainEvent, TenantId> _getTenantId = getTenantId ?? throw new ArgumentNullException(nameof(getTenantId));
    readonly TimeSpan _pollInterval = pollInterval is { Ticks: > 0 } value ? value : TimeSpan.Zero;
    readonly HashSet<TenantId> _active = [];

    ulong _offset;

    /// <inheritdoc />
    public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var record in _reader.ReadAsync(_pattern, _offset, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            _offset = EventStreamOffsets.GetNextOffset(_pattern, record);
            _ = Apply(record.Ev);
        }

        foreach (var tenantId in _active)
            yield return tenantId;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var sawAny = false;

            await foreach (var record in _reader.ReadAsync(_pattern, _offset, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                _offset = EventStreamOffsets.GetNextOffset(_pattern, record);
                sawAny = true;

                if (Apply(record.Ev) is { } change)
                    yield return change;
            }

            if (!sawAny && _pollInterval > TimeSpan.Zero)
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
        }
    }

    TenantLifecycleChange? Apply(DomainEvent ev)
    {
        switch (ev)
        {
            case TStartEvent:
                {
                    var tenantId = _getTenantId(ev);
                    _ = _active.Add(tenantId);
                    return new TenantLifecycleChange(TenantLifecycleChangeKind.Added, tenantId);
                }
            case TStopEvent:
                {
                    var tenantId = _getTenantId(ev);
                    _ = _active.Remove(tenantId);
                    return new TenantLifecycleChange(TenantLifecycleChangeKind.Removed, tenantId);
                }
            default:
                return null;
        }
    }
}
