using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

/// <summary>
///     Verifies that the public conformance suites actually fail a broken adapter. Portia ships these
///     for third-party stores to run against themselves, so a rejection path that never executes is a
///     suite that silently certifies an implementation which would lose or replay events in production.
///     Each case here breaks exactly one invariant and expects the matching violation.
/// </summary>
public sealed class ConformanceRejectionTests
{
    /// <summary>The single deduplication guarantee a defective probe breaks.</summary>
    public enum DeduplicationDefect
    {
        /// <summary>Breaks nothing; the positive control.</summary>
        None,

        /// <summary>Reports a never-before-seen event as already executed.</summary>
        ReportsFirstAsDuplicate,

        /// <summary>Runs the effect again for an event it already executed.</summary>
        ExecutesSequentialDuplicate,

        /// <summary>Lets more than one concurrent attempt through for one event.</summary>
        LosesConcurrentRace,

        /// <summary>Keeps its record of executed events only in memory.</summary>
        ForgetsAfterReopen
    }

    /// <summary>The single event-store invariant a defective probe breaks.</summary>
    public enum StoreDefect
    {
        /// <summary>Breaks nothing; the positive control.</summary>
        None,

        /// <summary>Yields a record for a stream that was never appended to.</summary>
        ReturnsRecordsForAbsentStream,

        /// <summary>Silently drops one appended record from a stream read.</summary>
        DropsAppendedRecords,

        /// <summary>Reports resource offsets that do not start at zero.</summary>
        ShiftsResourceOffsets,

        /// <summary>Returns a stream's records in reverse append order.</summary>
        ReordersRecords,

        /// <summary>Labels each record with a stream other than the one it was appended to.</summary>
        ReportsForeignStream,

        /// <summary>Replays a stream from the beginning however far in the caller asked to resume.</summary>
        IgnoresFromOffset,

        /// <summary>Appends regardless of the expected stream position.</summary>
        AcceptsStaleAppend,

        /// <summary>Rejects a stale append with its own exception type instead of the shared one.</summary>
        ThrowsAdapterExceptionOnConflict,

        /// <summary>Throws the right conflict but keeps the rejected events anyway.</summary>
        WritesDespiteConflict,

        /// <summary>Returns only one stream from a read that spans the whole area.</summary>
        PatternReadMissesStreams,

        /// <summary>Omits the area offset a projector checkpoints against.</summary>
        PatternReadOmitsAreaOffset,

        /// <summary>Returns pattern records whose scope offsets do not ascend.</summary>
        PatternReadIsUnordered
    }

    /// <summary>
    ///     Verifies that every event-store invariant the suite claims to check is one it will genuinely
    ///     reject: read coverage and ordering, zero-based contiguous offsets, the concrete source stream,
    ///     resuming from an offset, the shared concurrency exception, a conflict writing nothing, and a
    ///     pattern read that covers the area with ascending scope offsets.
    /// </summary>
    /// <param name="defect">The single invariant the store under test breaks.</param>
    /// <param name="expected">A distinctive fragment of the violation the suite must report.</param>
    [Theory]
    [InlineData(StoreDefect.ReturnsRecordsForAbsentStream, "never written")]
    [InlineData(StoreDefect.DropsAppendedRecords, "Expected 3 appended records")]
    [InlineData(StoreDefect.ShiftsResourceOffsets, "contiguous and zero-based")]
    [InlineData(StoreDefect.ReordersRecords, "preserve append order")]
    [InlineData(StoreDefect.ReportsForeignStream, "reported stream")]
    [InlineData(StoreDefect.IgnoresFromOffset, "records at and after it")]
    [InlineData(StoreDefect.AcceptsStaleAppend, "stale append succeeded")]
    [InlineData(StoreDefect.ThrowsAdapterExceptionOnConflict, "EventStreamConcurrencyException")]
    [InlineData(StoreDefect.WritesDespiteConflict, "must write nothing")]
    [InlineData(StoreDefect.PatternReadMissesStreams, "every stream in the area")]
    [InlineData(StoreDefect.PatternReadOmitsAreaOffset, "resumable cursor")]
    [InlineData(StoreDefect.PatternReadIsUnordered, "resume immediately")]
    public async Task ShouldRejectEventStoreProbeGivenOneBrokenInvariant(StoreDefect defect, string expected)
    {
        var exception = await Assert.ThrowsAsync<ConformanceViolationException>(() =>
            EventStoreConformance.VerifyAsync(new DefectiveEventStoreProbe(defect)).AsTask());

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Verifies the positive control for the table above: the same probe with no defect passes, so a
    ///     rejection above is evidence of the injected defect rather than of the harness itself.
    /// </summary>
    [Fact]
    public async Task ShouldAcceptEventStoreProbeWithNoInjectedDefect() =>
        await EventStoreConformance.VerifyAsync(new DefectiveEventStoreProbe(StoreDefect.None));

    /// <summary>
    ///     Verifies that the reaction-deduplication suite rejects each way a guard can fail to suppress a
    ///     duplicate: reporting the first execution as a duplicate, losing a concurrent race, and
    ///     forgetting what it had already executed once it reopens.
    /// </summary>
    /// <param name="defect">The single behavior the deduplication guard breaks.</param>
    /// <param name="expected">A distinctive fragment of the violation the suite must report.</param>
    [Theory]
    [InlineData(DeduplicationDefect.ReportsFirstAsDuplicate, "first reaction execution")]
    [InlineData(DeduplicationDefect.ExecutesSequentialDuplicate, "sequential duplicate")]
    [InlineData(DeduplicationDefect.LosesConcurrentRace, "concurrent duplicate")]
    [InlineData(DeduplicationDefect.ForgetsAfterReopen, "after the implementation reopened")]
    public async Task ShouldRejectDeduplicationProbeGivenOneBrokenGuarantee(DeduplicationDefect defect,
        string expected)
    {
        var exception = await Assert.ThrowsAsync<ConformanceViolationException>(() =>
            ReactionDeduplicationConformance.VerifyAsync(new DefectiveDeduplicationProbe(defect)).AsTask());

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Verifies the positive control for the deduplication table: an intact guard passes.
    /// </summary>
    [Fact]
    public async Task ShouldAcceptDeduplicationProbeWithNoInjectedDefect() =>
        await ReactionDeduplicationConformance.VerifyAsync(
            new DefectiveDeduplicationProbe(DeduplicationDefect.None));

    /// <summary>
    ///     Verifies that the projection-store suite rejects a probe whose live and rebuild identities do
    ///     not describe the same projection — the suite cannot prove generation isolation between two
    ///     unrelated projections, so it must refuse the probe rather than pass vacuously.
    /// </summary>
    /// <param name="rebuildId">The rebuild generation to give the second identity.</param>
    /// <param name="component">The component name to give the second identity.</param>
    [Theory]
    [InlineData(null, "conformance")]
    [InlineData("rebuild-1", "different-component")]
    public async Task ShouldRejectProjectionProbeGivenMismatchedLiveAndRebuildIdentities(string? rebuildId,
        string component)
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            ProjectionStoreConformance.VerifyAsync(new MismatchedIdentityProbe(rebuildId, component)).AsTask());

        Assert.Contains("matching live and rebuild identities", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that a store which refuses a stale writer but never applies the winner's data is
    ///     rejected: a conflict has to leave the winner committed, not roll both writers back.
    /// </summary>
    [Fact]
    public async Task ShouldRejectProjectionProbeGivenConflictDiscardsTheWinner()
    {
        using var probe = new ShapedProjectionProbe(ProjectionStoreShape.DiscardsWinner);

        var exception = await Assert.ThrowsAsync<ConformanceViolationException>(() =>
            ProjectionStoreConformance.VerifyAsync(probe).AsTask());

        Assert.Contains("stale checkpoint conflict", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that a store which compares the expected checkpoint only when a batch opens is
    ///     rejected: two batches opened before either commits would both pass that check, so the later
    ///     commit silently overwrites the earlier one.
    /// </summary>
    [Fact]
    public async Task ShouldRejectProjectionProbeGivenCheckpointComparedOnlyWhenBatchOpens()
    {
        using var probe = new ShapedProjectionProbe(ProjectionStoreShape.ChecksCheckpointOnlyAtBegin);

        var exception = await Assert.ThrowsAsync<ConformanceViolationException>(() =>
            ProjectionStoreConformance.VerifyAsync(probe).AsTask());

        Assert.Contains("stale projection checkpoint was allowed to commit", exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that a store which holds an exclusive lock from opening a batch until the batch ends
    ///     passes without deadlocking, even though the stale writer's open has to wait for the winner.
    /// </summary>
    [Fact]
    public async Task ShouldAcceptProjectionProbeGivenBatchOpenWaitsForAnExclusiveLock()
    {
        using var probe = new ShapedProjectionProbe(ProjectionStoreShape.LocksAtBegin);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await ProjectionStoreConformance.VerifyAsync(probe, timeout.Token);
    }

    sealed class DefectiveEventStoreProbe(StoreDefect defect) : IEventStoreConformanceProbe
    {
        InMemoryEventStore _store = new();

        public string Realm => "portia-conformance";

        public string Area => "rejection";

        public ValueTask ResetAsync(CancellationToken ct = default)
        {
            _store = new InMemoryEventStore();
            return ValueTask.CompletedTask;
        }

        public ValueTask<IEventStore> OpenAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IEventStore>(defect == StoreDefect.None
                ? _store
                : new DefectiveEventStore(_store, defect, Realm, Area));
    }

    // One decorator, one defect at a time: everything else delegates to a store the suite already
    // accepts, so a rejection can only come from the injected defect.
    sealed class DefectiveEventStore(InMemoryEventStore inner, StoreDefect defect, string realm, string area)
        : IEventStore
    {
        public async IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset = 0,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (defect == StoreDefect.ReturnsRecordsForAbsentStream)
            {
                yield return new DomainEventRecord(stream,
                    DomainEventSeed.Attach(new ConformanceEvent(1), Uuid.CreateVersion4(), 1), 0, new EventCursor("1"));
            }

            var records = new List<DomainEventRecord>();
            await foreach (var record in inner.ReadAsync(stream,
                               defect == StoreDefect.IgnoresFromOffset ? 0 : fromOffset, ct))
                records.Add(record);

            if (defect == StoreDefect.DropsAppendedRecords && records.Count > 0)
            {
                records.RemoveAt(records.Count - 1);
            }

            if (defect == StoreDefect.ReordersRecords)
            {
                // Keep the offsets ascending and swap only the events, so the suite is forced to judge
                // append order rather than tripping the offset check first.
                var events = records.Select(record => record.Event).Reverse().ToArray();
                records = [.. records.Select((record, index) => record with { Event = events[index] })];
            }

            foreach (var record in records)
            {
                yield return defect switch
                {
                    StoreDefect.ShiftsResourceOffsets => record with { ResourceOffset = record.ResourceOffset + 1 },
                    StoreDefect.ReportsForeignStream => record with
                    {
                        Stream = new EventStreamAddress(realm, area, "elsewhere")
                    },
                    _ => record
                };
            }
        }

        public async IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, EventCursor cursor,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var records = new List<DomainEventRecord>();
            await foreach (var record in inner.ReadAsync(pattern, cursor, ct))
                records.Add(record);

            if (defect == StoreDefect.PatternReadMissesStreams)
            {
                records.RemoveAll(record => record.Stream.Resource != "ordered");
            }

            if (defect == StoreDefect.PatternReadIsUnordered)
            {
                records.Reverse();
            }

            foreach (var record in records)
            {
                yield return defect == StoreDefect.PatternReadOmitsAreaOffset
                    ? record with { NextCursor = EventCursor.Start }
                    : record;
            }
        }

        public async ValueTask AppendAsync(EventStreamAddress stream, ulong expectedStreamPosition,
            IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
        {
            if (defect == StoreDefect.AcceptsStaleAppend)
            {
                var actual = 0UL;
                await foreach (var _ in inner.ReadAsync(stream, 0, ct))
                    actual++;
                await inner.AppendAsync(stream, actual, events, ct);
                return;
            }

            try
            {
                await inner.AppendAsync(stream, expectedStreamPosition, events, ct);
            }
            catch (EventStreamConcurrencyException) when (defect == StoreDefect.ThrowsAdapterExceptionOnConflict)
            {
                throw new InvalidOperationException("stale append");
            }
            catch (EventStreamConcurrencyException) when (defect == StoreDefect.WritesDespiteConflict)
            {
                var actual = 0UL;
                await foreach (var _ in inner.ReadAsync(stream, 0, ct))
                    actual++;
                await inner.AppendAsync(stream, actual, events, ct);
                throw;
            }
        }
    }

    sealed class DefectiveDeduplicationProbe(DeduplicationDefect defect) : IReactionDeduplicationProbe
    {
        readonly ConcurrentDictionary<Uuid, byte> _durable = new();
        int _concurrentReaderCount;

        TaskCompletionSource _concurrentReaders =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        Uuid? _sequentialEventId;
        ConcurrentDictionary<Uuid, byte> _session = new();

        public ValueTask ResetAsync(CancellationToken ct = default)
        {
            _durable.Clear();
            _session = new ConcurrentDictionary<Uuid, byte>();
            _sequentialEventId = null;
            _concurrentReaderCount = 0;
            _concurrentReaders = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return ValueTask.CompletedTask;
        }

        public ValueTask ReopenAsync(CancellationToken ct = default)
        {
            // A guard whose bookkeeping is only in memory forgets everything a restart discards.
            _session = new ConcurrentDictionary<Uuid, byte>();
            return ValueTask.CompletedTask;
        }

        public async ValueTask<bool> ExecuteAsync(Uuid eventId, Func<CancellationToken, ValueTask> effect,
            CancellationToken ct = default)
        {
            if (defect == DeduplicationDefect.ReportsFirstAsDuplicate)
            {
                return false;
            }

            var seen = defect == DeduplicationDefect.ForgetsAfterReopen ? _session : _durable;
            bool first;
            if (defect == DeduplicationDefect.LosesConcurrentRace)
            {
                _sequentialEventId ??= eventId;
                var already = seen.ContainsKey(eventId);
                if (eventId != _sequentialEventId)
                {
                    // Hold the first two concurrent readers until both have observed the empty slot.
                    // This injects the check-then-act defect deterministically even on a saturated runner.
                    if (Interlocked.Increment(ref _concurrentReaderCount) == 2)
                        _concurrentReaders.TrySetResult();
                    await _concurrentReaders.Task.WaitAsync(ct);
                }

                first = !already;
                seen[eventId] = 0;
            }
            else
            {
                first = defect == DeduplicationDefect.ExecutesSequentialDuplicate || seen.TryAdd(eventId, 0);
            }

            if (!first)
            {
                return false;
            }

            await effect(ct);
            return true;
        }
    }

    sealed class MismatchedIdentityProbe(string? rebuildId, string component) : IProjectionStoreConformanceProbe
    {
        public CheckpointIdentity LiveIdentity { get; } = new(
            "conformance", EventStreamPattern.ForPattern("testing", "projection"));

        public CheckpointIdentity RebuildIdentity { get; } = new(
            component, EventStreamPattern.ForPattern("testing", "projection"), rebuildId);

        public ValueTask ResetAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<IProjectionStoreConformanceSession> OpenSessionAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("The suite must reject the identities before opening a session.");
    }

    /// <summary>How a hand-written projection store coordinates two writers of one checkpoint.</summary>
    public enum ProjectionStoreShape
    {
        /// <summary>Refuses the stale writer but rolls the winner's committed batch back too.</summary>
        DiscardsWinner,

        /// <summary>Compares the expected checkpoint when a batch opens and never again at commit.</summary>
        ChecksCheckpointOnlyAtBegin,

        /// <summary>A conformant store that holds an exclusive lock from opening a batch until it ends.</summary>
        LocksAtBegin
    }

    sealed class ShapedProjectionProbe(ProjectionStoreShape shape) : IProjectionStoreConformanceProbe, IDisposable
    {
        readonly Lock _gate = new();
        readonly SemaphoreSlim _writer = new(1, 1);
        readonly Dictionary<CheckpointIdentity, (string? Value, ProjectionCheckpoint Checkpoint)> _states = [];

        public CheckpointIdentity LiveIdentity { get; } = new(
            "conformance", EventStreamPattern.ForPattern("testing", "projection"));

        public CheckpointIdentity RebuildIdentity { get; } = new(
            "conformance", EventStreamPattern.ForPattern("testing", "projection"), "rebuild-1");

        ProjectionStoreShape Shape => shape;

        public void Dispose() => _writer.Dispose();

        public ValueTask ResetAsync(CancellationToken ct = default)
        {
            lock (_gate)
                _states.Clear();
            return ValueTask.CompletedTask;
        }

        public ValueTask<IProjectionStoreConformanceSession> OpenSessionAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IProjectionStoreConformanceSession>(new Session(this));

        ProjectionCheckpoint CheckpointOf(CheckpointIdentity identity)
        {
            lock (_gate)
                return _states.GetValueOrDefault(identity).Checkpoint;
        }

        sealed class Session(ShapedProjectionProbe probe) : IProjectionStoreConformanceSession, IProjectionStore
        {
            readonly ShapedProjectionProbe _probe = probe;
            Batch? _batch;

            public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity,
                CancellationToken ct = default) =>
                ValueTask.FromResult(_probe.CheckpointOf(identity));

            public async ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context,
                CancellationToken ct = default)
            {
                var locked = false;
                if (_probe.Shape == ProjectionStoreShape.LocksAtBegin)
                {
                    await _probe._writer.WaitAsync(ct);
                    locked = true;
                }

                if (_probe.Shape != ProjectionStoreShape.DiscardsWinner
                    && _probe.CheckpointOf(context.Identity) != context.Checkpoint)
                {
                    if (locked)
                        _ = _probe._writer.Release();
                    throw new ProjectionConcurrencyException("stale checkpoint");
                }

                _batch = new Batch(this, context, locked);
                return _batch;
            }

            public IProjectionStore Store => this;

            public ValueTask StageValueAsync(string value, CancellationToken ct = default)
            {
                _batch!.Value = value;
                return ValueTask.CompletedTask;
            }

            public ValueTask<string?> ReadValueAsync(CheckpointIdentity identity, CancellationToken ct = default)
            {
                lock (_probe._gate)
                    return ValueTask.FromResult(_probe._states.GetValueOrDefault(identity).Value);
            }

            public ValueTask FailNextCommitAsync(CancellationToken ct = default)
            {
                _batch!.FailNext = true;
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            sealed class Batch(Session session, ProjectionBatchContext context, bool locked) : IProjectionBatch
            {
                bool _locked = locked;

                public string? Value { get; set; }

                public bool FailNext { get; set; }

                public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
                {
                    // Like a database transaction, a locking batch ends at commit whether or not it succeeds.
                    try
                    {
                        Commit(checkpoint);
                    }
                    finally
                    {
                        Unlock();
                    }

                    return ValueTask.CompletedTask;
                }

                public ValueTask DisposeAsync()
                {
                    Unlock();
                    return ValueTask.CompletedTask;
                }

                void Commit(ProjectionCheckpoint checkpoint)
                {
                    if (FailNext)
                    {
                        FailNext = false;
                        throw new IOException("injected commit failure");
                    }

                    var probe = session._probe;
                    lock (probe._gate)
                    {
                        var current = probe._states.GetValueOrDefault(context.Identity);
                        if (probe.Shape != ProjectionStoreShape.ChecksCheckpointOnlyAtBegin
                            && current.Checkpoint != context.Checkpoint)
                        {
                            if (probe.Shape == ProjectionStoreShape.DiscardsWinner)
                            {
                                // Rejects the loser, then drops the winner's committed batch as well.
                                _ = probe._states.Remove(context.Identity);
                            }

                            throw new ProjectionConcurrencyException("stale checkpoint");
                        }

                        probe._states[context.Identity] = (Value, checkpoint);
                    }
                }

                void Unlock()
                {
                    if (_locked)
                    {
                        _locked = false;
                        _ = session._probe._writer.Release();
                    }
                }
            }
        }
    }
}
