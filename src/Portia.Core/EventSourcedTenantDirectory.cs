using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

/// <summary>Replays tenant lifecycle events with independent progress for each watcher.</summary>
/// <typeparam name="TStartEvent">Marks a tenant active.</typeparam>
/// <typeparam name="TStopEvent">Marks a tenant inactive.</typeparam>
/// <param name="reader">Reads durable lifecycle events.</param>
/// <param name="pattern">The lifecycle stream pattern.</param>
/// <param name="getTenantId">Extracts the explicit tenant identity.</param>
/// <param name="pollInterval">Positive idle delay; defaults to one second.</param>
/// <param name="timeProvider">Schedules polling delays.</param>
/// <param name="notifier">Optional commit notifications used instead of idle polling.</param>
public sealed class EventSourcedTenantDirectory<TStartEvent, TStopEvent>(
    IDomainEventReader reader,
    EventStreamPattern pattern,
    Func<DomainEvent, TenantId> getTenantId,
    TimeSpan? pollInterval = null,
    TimeProvider? timeProvider = null,
    IDomainEventNotifier? notifier = null) : ITenantDirectory
    where TStartEvent : DomainEvent
    where TStopEvent : DomainEvent
{
    readonly IDomainEventReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    readonly EventStreamPattern _pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
    readonly Func<DomainEvent, TenantId> _getTenantId = getTenantId ?? throw new ArgumentNullException(nameof(getTenantId));
    readonly TimeSpan _pollInterval = GetInterval(pollInterval);
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    readonly IDomainEventNotifier? _notifier = notifier;

    /// <inheritdoc />
    public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var active = new HashSet<TenantId>();
        await foreach (var record in _reader.ReadAsync(_pattern, 0, ct).WithCancellation(ct).ConfigureAwait(false))
            _ = Apply(active, record.Event);
        foreach (var tenantId in active)
            yield return tenantId;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var subscription = _notifier is null
            ? null
            : await _notifier.SubscribeAsync(_pattern, ct).ConfigureAwait(false);
        var active = new HashSet<TenantId>();
        var initiallyRemoved = new HashSet<TenantId>();
        ulong offset = 0;
        var initial = true;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var sawAny = false;
            await foreach (var record in _reader.ReadAsync(_pattern, offset, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                offset = EventStreamOffsets.GetNextOffset(_pattern, record);
                sawAny = true;
                var change = Apply(active, record.Event, initial ? initiallyRemoved : null);
                if (!initial && change is { } delta)
                    yield return delta;
            }
            if (initial)
            {
                // Reconcile the gap after a preceding snapshot without replaying historical
                // start/stop cycles. Other snapshots and watchers never consume this cursor.
                foreach (var tenantId in active)
                    yield return new TenantLifecycleChange(TenantLifecycleChangeKind.Added, tenantId);
                foreach (var tenantId in initiallyRemoved)
                    yield return new TenantLifecycleChange(TenantLifecycleChangeKind.Removed, tenantId);
                initiallyRemoved.Clear();
                initial = false;
            }
            if (subscription is not null)
                await subscription.WaitAsync(ct).ConfigureAwait(false);
            else if (!sawAny)
                await Task.Delay(_pollInterval, _clock, ct).ConfigureAwait(false);
        }
    }

    TenantLifecycleChange? Apply(HashSet<TenantId> active, DomainEvent ev, HashSet<TenantId>? initiallyRemoved = null)
    {
        switch (ev)
        {
            case TStartEvent:
                var added = _getTenantId(ev);
                _ = initiallyRemoved?.Remove(added);
                return active.Add(added) ? new TenantLifecycleChange(TenantLifecycleChangeKind.Added, added) : null;
            case TStopEvent:
                var removed = _getTenantId(ev);
                _ = initiallyRemoved?.Add(removed);
                return active.Remove(removed) ? new TenantLifecycleChange(TenantLifecycleChangeKind.Removed, removed) : null;
            default:
                return null;
        }
    }

    static TimeSpan GetInterval(TimeSpan? pollInterval)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero, nameof(pollInterval));
        return interval;
    }
}
