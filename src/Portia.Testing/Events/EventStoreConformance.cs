namespace Cntryl.Portia.Testing;

/// <summary>
///     Reusable ordering, offset, and optimistic-concurrency checks for an
///     <see cref="IEventStore" />. These are the invariants aggregate hydration and every projector
///     depend on but cannot verify themselves — most importantly that a stale append fails with
///     <see cref="EventStreamConcurrencyException" /> rather than the backing store's own exception
///     type, which is what lets an application write one <c>catch</c> across every adapter.
/// </summary>
public static class EventStoreConformance
{
    /// <summary>Runs the complete event-store conformance suite.</summary>
    /// <param name="probe">An isolated implementation adapter.</param>
    /// <param name="ct">A token that can cancel verification.</param>
    /// <returns>A task that completes when every check has passed.</returns>
    /// <exception cref="ConformanceViolationException">The implementation violates a store invariant.</exception>
    public static async ValueTask VerifyAsync(IEventStoreConformanceProbe probe, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentException.ThrowIfNullOrWhiteSpace(probe.Realm);
        ArgumentException.ThrowIfNullOrWhiteSpace(probe.Area);
        await probe.ResetAsync(ct).ConfigureAwait(false);
        await VerifyEmptyStreamAsync(probe, ct).ConfigureAwait(false);
        await VerifyAppendAndReadOrderAsync(probe, ct).ConfigureAwait(false);
        await VerifyResumeFromOffsetAsync(probe, ct).ConfigureAwait(false);
        await VerifyStaleAppendConflictsAsync(probe, ct).ConfigureAwait(false);
        await VerifyConflictLeavesStreamUnchangedAsync(probe, ct).ConfigureAwait(false);
        await VerifyPatternReadCoversStreamsAsync(probe, ct).ConfigureAwait(false);
    }

    static async ValueTask VerifyEmptyStreamAsync(IEventStoreConformanceProbe probe, CancellationToken ct)
    {
        var store = await probe.OpenAsync(ct).ConfigureAwait(false);
        var records = await ReadAsync(store, Stream(probe, "absent"), 0, ct).ConfigureAwait(false);
        if (records.Count != 0)
        {
            throw new ConformanceViolationException(
                $"Reading a stream that was never written returned {records.Count} records; expected none.");
        }
    }

    static async ValueTask VerifyAppendAndReadOrderAsync(IEventStoreConformanceProbe probe, CancellationToken ct)
    {
        var store = await probe.OpenAsync(ct).ConfigureAwait(false);
        var stream = Stream(probe, "ordered");
        var id = Uuid.CreateVersion4();
        await store.AppendAsync(stream, 0, [Event(id, 1), Event(id, 2)], ct).ConfigureAwait(false);
        await store.AppendAsync(stream, 2, [Event(id, 3)], ct).ConfigureAwait(false);

        var records = await ReadAsync(store, stream, 0, ct).ConfigureAwait(false);
        if (records.Count != 3)
        {
            throw new ConformanceViolationException($"Expected 3 appended records; read {records.Count}.");
        }

        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (record.ResourceOffset != (ulong)index)
            {
                throw new ConformanceViolationException(
                    $"Record {index} reported resource offset {record.ResourceOffset}; offsets must be contiguous and zero-based.");
            }

            if (record.Event.Metadata.AggregateVersion != (ulong)(index + 1))
            {
                throw new ConformanceViolationException(
                    $"Record {index} reported aggregate version {record.Event.Metadata.AggregateVersion}; reads must preserve append order.");
            }

            if (record.Stream != stream)
            {
                throw new ConformanceViolationException(
                    $"Record {index} reported stream '{record.Stream}'; expected '{stream}'.");
            }
        }
    }

    static async ValueTask VerifyResumeFromOffsetAsync(IEventStoreConformanceProbe probe, CancellationToken ct)
    {
        var store = await probe.OpenAsync(ct).ConfigureAwait(false);
        var records = await ReadAsync(store, Stream(probe, "ordered"), 2, ct).ConfigureAwait(false);
        if (records.Count != 1 || records[0].ResourceOffset != 2)
        {
            throw new ConformanceViolationException(
                "Reading from an offset must return exactly the records at and after it, keeping their absolute offsets.");
        }
    }

    static async ValueTask VerifyStaleAppendConflictsAsync(IEventStoreConformanceProbe probe, CancellationToken ct)
    {
        var store = await probe.OpenAsync(ct).ConfigureAwait(false);
        var stream = Stream(probe, "conflict");
        var id = Uuid.CreateVersion4();
        await store.AppendAsync(stream, 0, [Event(id, 1)], ct).ConfigureAwait(false);

        // A second writer that still believes the stream is empty. Every implementation must
        // surface this as the one stable exception type applications catch.
        await RequireConflictAsync(store, stream, 0, Event(id, 1),
            "A stale append succeeded; the expected stream position was not enforced.", ct).ConfigureAwait(false);

        // A writer expecting a position the stream has not reached must be refused too, not
        // appended at the actual end or left with a gap.
        await RequireConflictAsync(store, stream, 5, Event(id, 6),
            "An append expecting a position ahead of the stream succeeded; the expected stream position was not enforced.",
            ct).ConfigureAwait(false);
    }

    static async ValueTask RequireConflictAsync(IEventStore store, EventStreamAddress stream,
        ulong expectedStreamPosition, DomainEvent domainEvent, string message, CancellationToken ct)
    {
        try
        {
            await store.AppendAsync(stream, expectedStreamPosition, [domainEvent], ct).ConfigureAwait(false);
        }
        catch (EventStreamConcurrencyException)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ConformanceViolationException(
                $"A stale append threw '{ex.GetType().FullName}'; it must throw {nameof(EventStreamConcurrencyException)} so one catch covers every adapter.");
        }

        throw new ConformanceViolationException(message);
    }

    static async ValueTask VerifyConflictLeavesStreamUnchangedAsync(IEventStoreConformanceProbe probe,
        CancellationToken ct)
    {
        var store = await probe.OpenAsync(ct).ConfigureAwait(false);
        var stream = Stream(probe, "conflict");
        var records = await ReadAsync(store, stream, 0, ct).ConfigureAwait(false);
        if (records.Count != 1)
        {
            throw new ConformanceViolationException(
                $"After a rejected append the stream holds {records.Count} records; a conflicting append must write nothing.");
        }
    }

    static async ValueTask VerifyPatternReadCoversStreamsAsync(IEventStoreConformanceProbe probe, CancellationToken ct)
    {
        var store = await probe.OpenAsync(ct).ConfigureAwait(false);

        // Same-named streams in another realm (another tenant) and another area of this realm must
        // stay out of the probe area's pattern read.
        var id = Uuid.CreateVersion4();
        await store.AppendAsync(new EventStreamAddress(probe.Realm + "-other", probe.Area, "ordered"), 0,
            [Event(id, 1)], ct).ConfigureAwait(false);
        await store.AppendAsync(new EventStreamAddress(probe.Realm, probe.Area + "-other", "ordered"), 0,
            [Event(id, 1)], ct).ConfigureAwait(false);

        var pattern = EventStreamPattern.ForPattern(probe.Realm, probe.Area);
        var records = await ReadAsync(store, pattern, EventCursor.Start, ct).ConfigureAwait(false);
        if (records.FirstOrDefault(record =>
                record.Stream.Realm != probe.Realm || record.Stream.Area != probe.Area) is { } foreign)
        {
            throw new ConformanceViolationException(
                $"A pattern read for '{pattern}' returned a record from stream '{foreign.Stream}' outside the pattern; it must never return another realm's or area's events.");
        }

        var streams = records.Select(record => record.Stream.Resource).Distinct().ToArray();
        if (!streams.Contains("ordered") || !streams.Contains("conflict"))
        {
            throw new ConformanceViolationException(
                $"A pattern read returned streams [{string.Join(", ", streams)}]; it must cover every stream in the area.");
        }

        if (records.Any(record => record.NextCursor == EventCursor.Start))
        {
            throw new ConformanceViolationException(
                "A pattern read must supply a resumable cursor for every record.");
        }

        if (records.Count > 1)
        {
            var resumed = await ReadAsync(store, pattern, records[0].NextCursor, ct).ConfigureAwait(false);
            if (!resumed.SequenceEqual(records.Skip(1)))
            {
                throw new ConformanceViolationException(
                    "A pattern read must resume immediately after the record that issued its cursor.");
            }
        }
    }

    static EventStreamAddress Stream(IEventStoreConformanceProbe probe, string resource) =>
        new(probe.Realm, probe.Area, resource);

    static ConformanceEvent Event(Uuid aggregateId, ulong version) => DomainEventSeed.Attach(
        new ConformanceEvent(version), aggregateId, version);

    static async ValueTask<List<DomainEventRecord>> ReadAsync(IEventStore store, EventStreamAddress stream,
        ulong fromOffset, CancellationToken ct)
    {
        var records = new List<DomainEventRecord>();
        await foreach (var record in store.ReadAsync(stream, fromOffset, ct).ConfigureAwait(false))
            records.Add(record);
        return records;
    }

    static async ValueTask<List<DomainEventRecord>> ReadAsync(IEventStore store, EventStreamPattern pattern,
        EventCursor cursor, CancellationToken ct)
    {
        var records = new List<DomainEventRecord>();
        await foreach (var record in store.ReadAsync(pattern, cursor, ct).ConfigureAwait(false))
            records.Add(record);
        return records;
    }
}
