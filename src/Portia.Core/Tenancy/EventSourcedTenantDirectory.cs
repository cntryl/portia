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
/// <param name="notifier">
///     Optional commit notifications that wake the directory early; it still re-reads after each
///     poll interval in case a notification was lost.
/// </param>
public sealed class EventSourcedTenantDirectory<TStartEvent, TStopEvent>(
    IDomainEventReader reader,
    EventStreamPattern pattern,
    Func<DomainEvent, TenantId> getTenantId,
    TimeSpan? pollInterval = null,
    TimeProvider? timeProvider = null,
    IDomainEventNotifier? notifier = null) : IResumableTenantDirectory
    where TStartEvent : DomainEvent
    where TStopEvent : DomainEvent
{
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    readonly Func<DomainEvent, TenantId> _getTenantId =
        getTenantId ?? throw new ArgumentNullException(nameof(getTenantId));

    readonly IDomainEventNotifier? _notifier = notifier;
    readonly EventStreamPattern _pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
    readonly TimeSpan _pollInterval = GetInterval(pollInterval);
    readonly IDomainEventReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <inheritdoc />
    public ValueTask<ITenantDirectoryCursor> OpenCursorAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ITenantDirectoryCursor>(new Cursor(this));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var active = new HashSet<TenantId>();
        await foreach (var record in ReadPassAsync(EventCursor.Start, active, "snapshot", "replay", ct)
                           .WithCancellation(ct)
                           .ConfigureAwait(false))
            _ = Apply(active, record.Event);
        foreach (var tenantId in active)
            yield return tenantId;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var subscription = _notifier is null
            ? null
            : await _notifier.SubscribeAsync(_pattern, ct).ConfigureAwait(false);
        var active = new HashSet<TenantId>();
        var initiallyRemoved = new HashSet<TenantId>();
        var cursor = EventCursor.Start;
        var initial = true;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var sawAny = false;
            await foreach (var record in ReadPassAsync(cursor, active, "watch", initial ? "replay" : "catch_up", ct)
                               .WithCancellation(ct)
                               .ConfigureAwait(false))
            {
                cursor = record.NextCursor;
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
            {
                await WaitForCommitAsync(subscription, ct).ConfigureAwait(false);
            }
            else if (!sawAny)
            {
                await Task.Delay(_pollInterval, _clock, ct).ConfigureAwait(false);
            }
        }
    }

    async Task WaitForCommitAsync(IDomainEventSubscription subscription, CancellationToken ct)
    {
        // CreateLinkedTokenSource takes no TimeProvider, so the poll deadline is its own source
        // scheduled on the directory clock and the linked one only combines it with the caller's token.
        using var deadline = new CancellationTokenSource(_pollInterval, _clock);
        using var backstop = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            await subscription.WaitAsync(backstop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            // A notification is only a wakeup. Periodically re-read durable state in case a
            // reconnect or bounded subscription buffer lost the signal.
        }
    }

    async IAsyncEnumerable<DomainEventRecord> ReadPassAsync(EventCursor cursor, HashSet<TenantId> active,
        string operation, string phase, [EnumeratorCancellation] CancellationToken ct)
    {
        var readDuration = TimeSpan.Zero;
        var eventCount = 0;
        var outcome = "interrupted";
        try
        {
            await using var records = _reader.ReadAsync(_pattern, cursor, ct).GetAsyncEnumerator(ct);
            while (true)
            {
                bool hasNext;
                var readStarted = _clock.GetTimestamp();
                try
                {
                    hasNext = await records.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    outcome = "canceled";
                    throw;
                }
                catch
                {
                    outcome = "fault";
                    throw;
                }
                finally
                {
                    readDuration += _clock.GetElapsedTime(readStarted);
                }

                if (!hasNext)
                {
                    outcome = "success";
                    break;
                }

                eventCount++;
                yield return records.Current;
            }
        }
        finally
        {
            PortiaTelemetry.TenantDirectoryReadFinished(readDuration, operation, phase, outcome, eventCount,
                active.Count);
        }
    }

    TenantLifecycleChange? Apply(HashSet<TenantId> active, DomainEvent ev, HashSet<TenantId>? initiallyRemoved = null)
    {
        if (ev is TStartEvent)
        {
            var added = _getTenantId(ev);
            _ = initiallyRemoved?.Remove(added);
            return active.Add(added) ? new TenantLifecycleChange(TenantLifecycleChangeKind.Added, added) : null;
        }

        if (ev is TStopEvent)
        {
            var removed = _getTenantId(ev);
            _ = initiallyRemoved?.Add(removed);
            return active.Remove(removed)
                ? new TenantLifecycleChange(TenantLifecycleChangeKind.Removed, removed)
                : null;
        }

        return null;
    }

    static TimeSpan GetInterval(TimeSpan? pollInterval)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero, nameof(pollInterval));
        return interval;
    }

    sealed class Cursor(EventSourcedTenantDirectory<TStartEvent, TStopEvent> directory) : ITenantDirectoryCursor
    {
        const string ConcurrentReadMessage = "A tenant directory cursor supports only one active enumeration.";

        readonly HashSet<TenantId> _active = [];
        int _disposed;
        bool _initial = true;
        TenantLifecycleChange[]? _initialChanges;
        int _initialIndex;
        EventCursor _nextCursor;
        int _reading;
        IDomainEventSubscription? _subscription;

        public async IAsyncEnumerable<TenantLifecycleChange> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
                throw new InvalidOperationException(ConcurrentReadMessage);

            IDomainEventSubscription? subscription = null;
            try
            {
                subscription = directory._notifier is null
                    ? null
                    : await directory._notifier.SubscribeAsync(directory._pattern, ct).ConfigureAwait(false);
                Volatile.Write(ref _subscription, subscription);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    if (subscription is not null &&
                        ReferenceEquals(Interlocked.CompareExchange(ref _subscription, null, subscription),
                            subscription))
                    {
                        await subscription.DisposeAsync().ConfigureAwait(false);
                    }

                    throw new ObjectDisposedException(GetType().FullName);
                }

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    if (_initialChanges is not null)
                    {
                        while (_initialIndex < _initialChanges.Length)
                            yield return _initialChanges[_initialIndex++];
                        _initialChanges = null;
                    }

                    var sawAny = false;
                    await foreach (var record in directory.ReadPassAsync(_nextCursor, _active, "cursor",
                                           _initial ? "replay" : "catch_up", ct)
                                       .WithCancellation(ct).ConfigureAwait(false))
                    {
                        _nextCursor = record.NextCursor;
                        sawAny = true;
                        var change = directory.Apply(_active, record.Event);
                        if (!_initial && change is { } delta)
                            yield return delta;
                    }

                    if (_initial)
                    {
                        _initial = false;
                        _initialChanges = _active.OrderBy(tenant => tenant.Value, StringComparer.Ordinal)
                            .Select(tenant => new TenantLifecycleChange(TenantLifecycleChangeKind.Added, tenant))
                            .ToArray();
                        _initialIndex = 0;
                        continue;
                    }

                    if (subscription is not null)
                    {
                        await directory.WaitForCommitAsync(subscription, ct).ConfigureAwait(false);
                    }
                    else if (!sawAny)
                    {
                        await Task.Delay(directory._pollInterval, directory._clock, ct).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                if (subscription is not null &&
                    ReferenceEquals(Interlocked.CompareExchange(ref _subscription, null, subscription), subscription))
                {
                    await subscription.DisposeAsync().ConfigureAwait(false);
                }

                Volatile.Write(ref _reading, 0);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (Interlocked.Exchange(ref _subscription, null) is { } subscription)
                await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }
}
