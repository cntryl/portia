using System.Collections.Concurrent;

namespace Cntryl.Portia;

/// <summary>Verifies the public backend-neutral conformance suites.</summary>
public sealed class TestingConformanceTests
{
    /// <summary>A durable deduplication primitive executes one effect per event identity.</summary>
    [Fact]
    public async Task ShouldAcceptProbeGivenDurableConcurrentReactionDeduplication() =>
        await ReactionDeduplicationConformance.VerifyAsync(new CorrectDeduplicationProbe());

    /// <summary>A primitive that executes duplicates is rejected.</summary>
    [Fact]
    public async Task ShouldRejectProbeGivenReactionDuplicateExecutesAgain()
    {
        var exception = await Assert.ThrowsAsync<ConformanceViolationException>(() =>
            ReactionDeduplicationConformance.VerifyAsync(new BrokenDeduplicationProbe()).AsTask());

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A projection repository with atomic commits and isolated generations passes.</summary>
    [Fact]
    public async Task ShouldAcceptProbeGivenAtomicProjectionStoreSemantics() =>
        await ProjectionStoreConformance.VerifyAsync(new ProjectionProbe(leakOnDispose: false));

    /// <summary>A projection repository that exposes uncommitted data is rejected.</summary>
    [Fact]
    public async Task ShouldRejectProbeGivenProjectionDisposalLeaksChanges()
    {
        var exception = await Assert.ThrowsAsync<ConformanceViolationException>(() =>
            ProjectionStoreConformance.VerifyAsync(new ProjectionProbe(leakOnDispose: true)).AsTask());

        Assert.Contains("uncommitted", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A projection adapter must translate stale commits to the shared exception.</summary>
    [Fact]
    public async Task ShouldRejectProbeGivenProjectionConflictUsesAdapterException()
    {
        var exception = await Assert.ThrowsAsync<ConformanceViolationException>(() =>
            ProjectionStoreConformance.VerifyAsync(new ProjectionProbe(leakOnDispose: false, translateConflict: false)).AsTask());

        Assert.Contains(nameof(ProjectionConcurrencyException), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>The in-memory event store honors ordering, offsets, and stream concurrency.</summary>
    [Fact]
    public async Task ShouldAcceptProbeGivenConformantEventStore() =>
        await EventStoreConformance.VerifyAsync(new EventStoreProbe(enforceExpectedPosition: true));

    /// <summary>A store that accepts a stale append is rejected.</summary>
    [Fact]
    public async Task ShouldRejectProbeGivenEventStoreIgnoresExpectedStreamPosition()
    {
        var exception = await Assert.ThrowsAsync<ConformanceViolationException>(() =>
            EventStoreConformance.VerifyAsync(new EventStoreProbe(enforceExpectedPosition: false)).AsTask());

        Assert.Contains("stale append", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    sealed class EventStoreProbe(bool enforceExpectedPosition) : IEventStoreConformanceProbe
    {
        InMemoryEventStore _store = new();
        public string Realm => "portia-conformance";
        public string Area => "event-store";
        public ValueTask ResetAsync(CancellationToken ct = default) { _store = new InMemoryEventStore(); return ValueTask.CompletedTask; }
        public ValueTask<IEventStore> OpenAsync(CancellationToken ct = default) => ValueTask.FromResult<IEventStore>(
            enforceExpectedPosition ? _store : new PermissiveEventStore(_store));
    }

    // Drops the optimistic-concurrency check the suite exists to prove, so the negative test
    // shows the suite fails a store that would silently lose a concurrent writer's events.
    sealed class PermissiveEventStore(InMemoryEventStore inner) : IEventStore
    {
        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset = 0, CancellationToken ct = default)
            => inner.ReadAsync(stream, fromOffset, ct);
        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, ulong fromOffset = 0, CancellationToken ct = default)
            => inner.ReadAsync(pattern, fromOffset, ct);
        public async ValueTask AppendAsync(EventStreamAddress stream, ulong expectedStreamPosition,
            IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
        {
            var actual = 0UL;
            await foreach (var _ in inner.ReadAsync(stream, 0, ct)) actual++;
            await inner.AppendAsync(stream, actual, events, ct);
        }
    }

    sealed class CorrectDeduplicationProbe : IReactionDeduplicationProbe
    {
        readonly ConcurrentDictionary<Uuid, byte> _seen = new();
        public ValueTask ResetAsync(CancellationToken ct = default) { _seen.Clear(); return ValueTask.CompletedTask; }
        public ValueTask ReopenAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async ValueTask<bool> ExecuteAsync(Uuid eventId, Func<CancellationToken, ValueTask> effect, CancellationToken ct = default)
        {
            if (!_seen.TryAdd(eventId, 0))
                return false;
            await effect(ct);
            return true;
        }
    }

    sealed class BrokenDeduplicationProbe : IReactionDeduplicationProbe
    {
        public ValueTask ResetAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ReopenAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async ValueTask<bool> ExecuteAsync(Uuid eventId, Func<CancellationToken, ValueTask> effect, CancellationToken ct = default)
        {
            await effect(ct);
            return true;
        }
    }

    sealed class ProjectionProbe(bool leakOnDispose, bool translateConflict = true) : IProjectionStoreConformanceProbe
    {
        readonly Lock _gate = new();
        readonly Dictionary<CheckpointIdentity, ProjectionState> _states = [];

        public CheckpointIdentity LiveIdentity { get; } = new(
            "conformance", EventStreamPattern.ForPattern("testing", "projection"));

        public CheckpointIdentity RebuildIdentity { get; } = new(
            "conformance", EventStreamPattern.ForPattern("testing", "projection"), "rebuild-1");

        public ValueTask ResetAsync(CancellationToken ct = default)
        {
            lock (_gate) _states.Clear();
            return ValueTask.CompletedTask;
        }

        public ValueTask<IProjectionStoreConformanceSession> OpenSessionAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IProjectionStoreConformanceSession>(new ProjectionSession(this, leakOnDispose));

        internal ProjectionState Read(CheckpointIdentity identity)
        {
            lock (_gate) return _states.GetValueOrDefault(identity) ?? new ProjectionState(null, ProjectionCheckpoint.Start);
        }

        internal void Commit(ProjectionBatchContext context, string? value, ProjectionCheckpoint checkpoint)
        {
            lock (_gate)
            {
                var current = _states.GetValueOrDefault(context.Identity) ?? new ProjectionState(null, ProjectionCheckpoint.Start);
                if (current.Checkpoint != context.Checkpoint)
                {
                    if (!translateConflict)
                        throw new InvalidOperationException("stale checkpoint");
                    throw new ProjectionConcurrencyException("stale checkpoint");
                }
                _states[context.Identity] = new ProjectionState(value, checkpoint);
            }
        }

        internal void Leak(ProjectionBatchContext context, string? value)
        {
            lock (_gate)
            {
                var current = _states.GetValueOrDefault(context.Identity) ?? new ProjectionState(null, ProjectionCheckpoint.Start);
                _states[context.Identity] = current with { Value = value };
            }
        }
    }

    sealed class ProjectionSession(ProjectionProbe probe, bool leakOnDispose)
        : IProjectionStoreConformanceSession, IProjectionStore
    {
        readonly ProjectionProbe _probe = probe;
        ProjectionBatch? _batch;
        bool _failNextCommit;

        public IProjectionStore Store => this;

        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct = default) =>
            ValueTask.FromResult(_probe.Read(identity).Checkpoint);

        public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default)
        {
            if (_batch is not null)
                throw new InvalidOperationException("batch already active");
            _batch = new ProjectionBatch(this, context, leakOnDispose);
            return ValueTask.FromResult<IProjectionBatch>(_batch);
        }

        public ValueTask StageValueAsync(string value, CancellationToken ct = default)
        {
            (_batch ?? throw new InvalidOperationException("no active batch")).Value = value;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> ReadValueAsync(CheckpointIdentity identity, CancellationToken ct = default) =>
            ValueTask.FromResult(_probe.Read(identity).Value);

        public ValueTask FailNextCommitAsync(CancellationToken ct = default)
        {
            _failNextCommit = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _batch = null;
            return ValueTask.CompletedTask;
        }

        sealed class ProjectionBatch(ProjectionSession session, ProjectionBatchContext context, bool leakOnDispose)
            : IProjectionBatch
        {
            bool _committed;
            public string? Value { get; set; }

            public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
            {
                if (session._failNextCommit)
                {
                    session._failNextCommit = false;
                    throw new InvalidOperationException("injected commit failure");
                }
                session._probe.Commit(context, Value, checkpoint);
                _committed = true;
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                if (!_committed && leakOnDispose)
                    session._probe.Leak(context, Value);
                session._batch = null;
                return ValueTask.CompletedTask;
            }
        }
    }

    internal sealed record ProjectionState(string? Value, ProjectionCheckpoint Checkpoint);
}
