namespace Cntryl.Portia;

// A session exists only inside one coordinator ownership callback. All durable changes are fenced
// by that session. Each write reloads the document so concurrent operator approval cannot be lost.
sealed class OperationalSession(
    IOperationalStore store,
    OperationalOptions options,
    IApplicationTopology topology,
    TimeProvider clock)
{
    readonly Guid _owner = Guid.NewGuid();
    OperationalTraceBuffer _trace = new(options.MaxTraceEntries);
    OperationalTarget _target = null!;
    OperationalProcessorState _state = null!;
    Guid? _attempt;
    Guid? _repairPlan;
    bool _failureRecorded;

    internal OperationalProcessorState State => _state;
    internal void BeginPass() => _failureRecorded = false;

    internal async ValueTask PassSucceededAsync(CancellationToken ct)
    {
        if (ActivePlan is { Action: RepairActions.Resume } resume)
        {
            await ChangeAsync(state =>
            {
                if (!state.Repairs.Any(r => r.PlanId == resume.Id && r.Status == OperationalRepairStatuses.Running))
                    return state;
                var now = clock.GetUtcNow();
                try
                {
                    _ = PortiaOperations.RequireCurrentPlan(state, resume.Id, now);
                    PortiaOperations.RequireApproval(state, resume.Id, now);
                }
                catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
                {
                    return CompleteRepair(state, OperationalRepairStatuses.Invalidated, null, state.Checkpoint);
                }

                return CompleteRepair(state with { Block = null, ConsecutiveFailures = 0 },
                    OperationalRepairStatuses.Resumed, null, state.Checkpoint);
            }, ct).ConfigureAwait(false);
            _repairPlan = null;
            return;
        }

        if (_state.ConsecutiveFailures != 0)
            await ChangeAsync(state => state with { ConsecutiveFailures = 0 }, ct).ConfigureAwait(false);
    }

    internal RepairPlan? ActivePlan => _repairPlan is { } id ? _state.Plans.Single(p => p.Id == id) : null;
    internal IReadOnlyList<OperationalSource>? InterruptedSources { get; private set; }

    internal void EnsureRecoveryCompleted(ProjectionCheckpoint checkpoint)
    {
        if (InterruptedSources is { Count: > 0 }
            || ActivePlan is { Action: RepairActions.Retry } retry &&
            _state.Repairs.Any(r => r.PlanId == retry.Id && r.Status == OperationalRepairStatuses.Running))
            throw new ProcessorFailureException(_target.Identity, checkpoint, ProcessorFailureStage.Reading,
                error: RequestError.Validation("The original recovery source set is unavailable."));
    }

    internal async ValueTask AcquireAsync(OperationalTarget target, string kind,
        OperationalProcessingSettings settings, ProjectionCheckpoint? checkpoint, CancellationToken ct)
    {
        _target = target;
        await RequiredAsync(async () =>
        {
            var prior = await store.ReadAsync(target, ct).ConfigureAwait(false);
            var cursor = checkpoint is { } loaded ? loaded.Cursor.Value : prior?.Checkpoint;
            var latest = prior is { Attempts.Count: > 0 } ? prior.Attempts[^1] : null;
            InterruptedSources = latest is { Outcome: OperationalAttemptOutcomes.Running or OperationalAttemptOutcomes.Interrupted }
                                 && latest.ExpectedCheckpoint == cursor
                ? latest.Sources
                : null;
            var now = clock.GetUtcNow();
            // Only a durable attempt start permits automatic replay after losing the repair owner.
            var replayInterruptedRepair = InterruptedSources is { Count: > 0 } &&
                                          prior?.Repairs.Any(r => r.Status == OperationalRepairStatuses.Running) == true;
            var next = prior is null
                ? new OperationalProcessorState(target, kind, _owner, 1, cursor, settings,
                    [], null, [], [], [], 0, now)
                : prior with
                {
                    OwnerSession = _owner,
                    Revision = checked(prior.Revision + 1),
                    Settings = settings,
                    Checkpoint = cursor,
                    Attempts = prior.Attempts.Select(a => a.Outcome == OperationalAttemptOutcomes.Running
                        ? a with
                        {
                            Outcome =
                            kind == ProcessorKinds.Projector && checkpoint is { } native &&
                            a.ExpectedCheckpoint != native.Cursor.Value
                                ? OperationalAttemptOutcomes.Indeterminate
                                : OperationalAttemptOutcomes.Interrupted,
                            CompletedAt = now,
                            Coverage = a.Coverage + "; process interrupted before confirmation"
                        }
                        : a).ToArray(),
                    Repairs = prior.Repairs.Select(r =>
                        r.Status is OperationalRepairStatuses.Queued or OperationalRepairStatuses.Running
                            ? r with
                            {
                                Status = r.Status == OperationalRepairStatuses.Running
                                    ? OperationalRepairStatuses.Indeterminate
                                    : OperationalRepairStatuses.Invalidated
                            }
                            : r).ToArray(),
                    Block = replayInterruptedRepair ? null : prior.Block
                };
            if (!await store.CompareExchangeAsync(next, prior?.Revision ?? 0, prior?.OwnerSession ?? Guid.Empty, ct)
                    .ConfigureAwait(false))
                return false;
            _state = next;
            return true;
        }, ct).ConfigureAwait(false);
    }

    // Always reload native progress, including after commit acknowledgement or confirmation loss.
    internal async ValueTask<bool> PrepareAsync(ProjectionCheckpoint checkpoint, CancellationToken ct)
    {
        _failureRecorded = false;
        if (InterruptedSources is { Count: > 0 } && _state.Checkpoint != checkpoint.Cursor.Value)
            InterruptedSources = null;
        await ChangeAsync(state =>
        {
            if (state.Checkpoint == checkpoint.Cursor.Value)
                return state;
            var now = clock.GetUtcNow();
            return state with
            {
                Checkpoint = checkpoint.Cursor.Value,
                Attempts = state.Attempts.Select(a =>
                    a.Outcome is OperationalAttemptOutcomes.Running or OperationalAttemptOutcomes.Failed
                        or OperationalAttemptOutcomes.Interrupted
                    && a.ExpectedCheckpoint != checkpoint.Cursor.Value
                        ? a with
                        {
                            Outcome = OperationalAttemptOutcomes.Indeterminate,
                            CompletedAt = a.CompletedAt ?? now,
                            Coverage = a.Coverage + "; authoritative progress differs from historical confirmation"
                        }
                        : a).ToArray(),
                // Native progress supersedes this retry's source set without proving its outcome.
                Repairs = state.Repairs.Select(repair => repair.Status == OperationalRepairStatuses.Running
                                                         && state.Plans.Any(plan => plan.Id == repair.PlanId
                                                             && plan.ExpectedCheckpoint != checkpoint.Cursor.Value)
                    ? repair with { Status = OperationalRepairStatuses.Indeterminate }
                    : repair).ToArray()
            };
        }, ct).ConfigureAwait(false);
        if (_repairPlan is { } active &&
            !_state.Repairs.Any(r => r.PlanId == active && r.Status == OperationalRepairStatuses.Running))
            _repairPlan = null;
        if (_repairPlan is { } running && !_state.Approvals.Any(a => a.PlanId == running
                                                                     && !a.Revoked && a.ExpiresAt > clock.GetUtcNow()))
        {
            await ChangeAsync(
                    state => CompleteRepair(state, OperationalRepairStatuses.Invalidated, null, state.Checkpoint), ct)
                .ConfigureAwait(false);
            _repairPlan = null;
            return false;
        }

        if (_state.Block is null)
        {
            if (_state.ConsecutiveFailures > 0 && _state.Attempts.Count > 0 && _state.Attempts[^1] is
                { Outcome: OperationalAttemptOutcomes.Failed, CompletedAt: { } ended })
            {
                var settings = _state.Settings;
                if (clock.GetUtcNow() < ended + WorkloadFailureBackoff.Delay(
                        TimeSpan.FromTicks(settings.PollIntervalTicks),
                        TimeSpan.FromTicks(settings.MaximumFailureDelayTicks), _state.ConsecutiveFailures))
                    return false;
            }

            return true;
        }

        if (_repairPlan is not null)
            return true;
        var queued = _state.Repairs.FirstOrDefault(r => r.Status == OperationalRepairStatuses.Queued);
        if (queued is null)
            return false;
        RepairPlan? claimed = null;
        await ChangeAsync(state =>
        {
            try
            {
                claimed = PortiaOperations.RequireCurrentPlan(state, queued.PlanId, clock.GetUtcNow());
                PortiaOperations.RequireApproval(state, queued.PlanId, clock.GetUtcNow());
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
            {
                return state with
                {
                    Repairs = state.Repairs.Select(r => r.Id == queued.Id
                        ? r with { Status = OperationalRepairStatuses.Invalidated }
                        : r).ToArray()
                };
            }

            return state with
            {
                ConsecutiveFailures = 0,
                Repairs = state.Repairs.Select(r => r.Id == queued.Id
                    ? r with { Status = OperationalRepairStatuses.Running }
                    : r).ToArray()
            };
        }, ct).ConfigureAwait(false);
        if (!_state.Repairs.Any(r => r.Id == queued.Id && r.Status == OperationalRepairStatuses.Running))
            return false;
        _repairPlan = queued.PlanId;
        var failedAttempt = _state.Attempts.Single(a => a.Id == claimed!.FailedAttemptId);
        if (claimed!.Action == RepairActions.Resume && failedAttempt.Outcome is
            OperationalAttemptOutcomes.CommittedCleanupFailed or OperationalAttemptOutcomes.Indeterminate)
        {
            await ChangeAsync(state => CompleteRepair(state with { Block = null, ConsecutiveFailures = 0 },
                failedAttempt.Outcome == OperationalAttemptOutcomes.Indeterminate
                    ? OperationalRepairStatuses.Indeterminate
                    : OperationalRepairStatuses.Committed,
                failedAttempt.Id, checkpoint.Cursor.Value), ct).ConfigureAwait(false);
            _repairPlan = null;
        }

        return true;
    }

    internal ProjectionRunOptions BoundOptions(ProjectionRunOptions normal)
    {
        var sources = ActivePlan?.Sources ?? InterruptedSources;
        return sources is { Count: > 0 }
            ? normal with { MaxBatchSize = sources.Count, MaxEventsPerPass = sources.Count }
            : normal;
    }

    internal async ValueTask<Guid> BeginAsync(IReadOnlyList<DomainEventRecord> records,
        ProjectionCheckpoint checkpoint, CancellationToken ct)
    {
        var sources = records.Select(OperationalSource.From).ToArray();
        var expected = ActivePlan?.Sources ?? InterruptedSources;
        if (expected is { Count: > 0 } && !expected.SequenceEqual(sources))
            throw new ProcessorFailureException(_target.Identity, checkpoint, ProcessorFailureStage.Handling,
                error: RequestError.Validation("The original recovery source set changed before invocation."));
        var id = Guid.NewGuid();
        var started = clock.GetUtcNow();
        _trace = new OperationalTraceBuffer(options.MaxTraceEntries);
        await ChangeAsync(state =>
        {
            if (_repairPlan is { } planId)
            {
                _ = PortiaOperations.RequireCurrentPlan(state, planId, clock.GetUtcNow());
                PortiaOperations.RequireApproval(state, planId, clock.GetUtcNow());
            }
            else if (state.Block is not null)
                throw new InvalidOperationException("The processor is durably blocked.");

            // A durable start whose acknowledgement was lost is this same pending invocation.
            // Reconfirm it after reloading instead of appending its identity a second time.
            if (state.Attempts.SingleOrDefault(candidate => candidate.Id == id) is { } existing)
            {
                if (existing.OwnerSession != _owner || existing.StartedAt != started
                                                    || existing.Outcome != OperationalAttemptOutcomes.Running ||
                                                    existing.ExpectedCheckpoint != checkpoint.Cursor.Value
                                                    || existing.NextCheckpoint != sources[^1].NextCursor ||
                                                    !existing.Sources.SequenceEqual(sources))
                    throw new InvalidDataException("The persisted attempt start does not match this invocation.");
                return state;
            }

            var attempt = new OperationalAttempt(id, _owner, started, null, OperationalAttemptOutcomes.Running,
                checkpoint.Cursor.Value,
                sources[^1].NextCursor, sources, [],
                "local awaited generated dispatch; opaque dependencies and detached work unknown",
                topology.Describe());
            return state with { Attempts = [.. state.Attempts, attempt] };
        }, ct).ConfigureAwait(false);
        _attempt = id;
        return id;
    }

    internal IDisposable Observe()
    {
        // Detached children can outlive a handler. Their ambient sink must keep the original
        // buffer, rather than append to the next attempt's evidence.
        var trace = _trace;
        return ProcessingObservation.Capture(trace.Record);
    }

    internal async ValueTask CommitReactionAsync(IOperationalReactionCheckpointStore checkpoints,
        ProjectionCheckpoint expected, ProjectionCheckpoint next, CancellationToken ct)
    {
        var id = _attempt ?? throw new InvalidOperationException("A durable attempt start is required.");
        await RequiredAsync(async () =>
        {
            var state = await ReadOwnedAsync(ct).ConfigureAwait(false);
            // A lost commit acknowledgement is settled by the atomically stored receipt.
            if (state.Attempts.Single(a => a.Id == id).Outcome == OperationalAttemptOutcomes.Committed)
            {
                _state = state;
                return true;
            }

            var replacement = Succeed(state, id, next.Cursor.Value) with { Revision = checked(state.Revision + 1) };
            if (!await checkpoints.CommitAsync(replacement, state.Revision, _owner, id, expected.Cursor.Value,
                    next.Cursor.Value, ct).ConfigureAwait(false))
                return false;
            _state = replacement;
            return true;
        }, ct).ConfigureAwait(false);
        _attempt = null;
        _repairPlan = null;
        InterruptedSources = null;
    }

    internal async ValueTask ConfirmProjectionAsync(ProjectionCheckpoint next, CancellationToken ct)
    {
        var id = _attempt ?? throw new InvalidOperationException("A durable attempt start is required.");
        await ChangeAsync(state => Succeed(state, id, next.Cursor.Value), ct).ConfigureAwait(false);
        _attempt = null;
        _repairPlan = null;
        InterruptedSources = null;
    }

    internal async ValueTask RecordFailureAsync(Exception exception, ProjectionCheckpoint? committed,
        CancellationToken ct)
    {
        if (_failureRecorded)
            return;
        var diagnostic = exception as ProcessorFailureException;
        var id = _attempt ?? Guid.NewGuid();
        await ChangeAsync(state =>
        {
            // Runner completion is recorded once, even when hosting sees the same exception.
            if (state.Attempts.Any(a => a.Id == id && a.Outcome != OperationalAttemptOutcomes.Running))
                return state;
            var now = clock.GetUtcNow();
            var prior = state.Attempts.SingleOrDefault(a => a.Id == id)
                        ?? new OperationalAttempt(id, _owner, now, null, OperationalAttemptOutcomes.Running,
                            state.Checkpoint, null,
                            ActivePlan?.Sources ?? InterruptedSources ?? [], [], "no handler invocation recorded",
                            topology.Describe());
            var transient = diagnostic?.IsTransient ?? true;
            var failures = checked(state.ConsecutiveFailures + 1);
            var terminal = committed is not null || !transient || failures >= state.Settings.FailureAttemptLimit;
            var attempt = prior with
            {
                CompletedAt = now,
                Outcome = committed is null
                    ? OperationalAttemptOutcomes.Failed
                    : OperationalAttemptOutcomes.CommittedCleanupFailed,
                Trace = _attempt is null ? [] : TraceSnapshot(),
                Coverage = _attempt is null ? prior.Coverage : Coverage(prior),
                FailureStage = diagnostic?.Stage.ToString() ?? "Pass",
                FailureKind = diagnostic?.Error?.Kind.ToString() ?? exception.GetType().Name,
                IsTransient = transient,
                NextCheckpoint = committed?.Cursor.Value ?? prior.NextCheckpoint
            };
            var next = state with
            {
                Checkpoint = committed?.Cursor.Value ?? state.Checkpoint,
                ConsecutiveFailures = failures,
                Attempts = [.. state.Attempts.Where(a => a.Id != id), attempt],
                Block = terminal ? new OperationalBlock(id, checked(state.Revision + 1)) : state.Block
            };
            return terminal
                ? CompleteRepair(next,
                    committed is null ? OperationalRepairStatuses.Failed : OperationalRepairStatuses.Committed, id,
                    committed?.Cursor.Value)
                : next;
        }, ct).ConfigureAwait(false);
        _attempt = null;
        _failureRecorded = true;
    }

    OperationalProcessorState Succeed(OperationalProcessorState state, Guid id, string? checkpoint)
        => InvalidateObsoleteQueuedRepairs(CompleteRepair(state with
        {
            Checkpoint = checkpoint,
            ConsecutiveFailures = _repairPlan is null ? state.ConsecutiveFailures : 0,
            Block = null,
            Attempts = state.Attempts.Select(a => a.Id == id
                ? a with
                {
                    Outcome = OperationalAttemptOutcomes.Committed,
                    CompletedAt = clock.GetUtcNow(),
                    NextCheckpoint = checkpoint,
                    Trace = TraceSnapshot(),
                    Coverage = Coverage(a)
                }
                : a).ToArray()
        }, OperationalRepairStatuses.Committed, id, checkpoint));

    OperationalProcessorState CompleteRepair(OperationalProcessorState state, string status, Guid? attempt,
        string? checkpoint)
        => _repairPlan is not { } id
            ? state
            : state with
            {
                Repairs = state.Repairs.Select(r => r.PlanId == id
                    ? r with { Status = status, AttemptId = attempt, Checkpoint = checkpoint }
                    : r).ToArray()
            };

    OperationalProcessorState InvalidateObsoleteQueuedRepairs(OperationalProcessorState state)
    {
        OperationalRepair[]? repairs = null;
        for (var index = 0; index < state.Repairs.Count; index++)
        {
            var repair = state.Repairs[index];
            if (repair.Status != OperationalRepairStatuses.Queued)
                continue;
            try
            {
                _ = PortiaOperations.RequireCurrentPlan(state, repair.PlanId, clock.GetUtcNow());
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
            {
                // Queued work has not started. Its exact plan becoming stale settles it without
                // changing the outcome of a running or indeterminate repair, or clearing a block.
                repairs ??= [.. state.Repairs];
                repairs[index] = repair with { Status = OperationalRepairStatuses.Invalidated };
            }
        }

        return repairs is null ? state : state with { Repairs = repairs };
    }

    OperationalTraceEntry[] TraceSnapshot() => _trace.Snapshot();

    string Coverage(OperationalAttempt attempt) =>
        attempt.Coverage + (_trace.Limited ? "; trace limit reached" : string.Empty);

    async ValueTask<OperationalProcessorState> ReadOwnedAsync(CancellationToken ct)
    {
        var state = await store.ReadAsync(_target, ct).ConfigureAwait(false);
        if (state?.OwnerSession != _owner)
            throw new OperationalOwnershipException();
        return state;
    }

    async ValueTask ChangeAsync(Func<OperationalProcessorState, OperationalProcessorState> change, CancellationToken ct)
    {
        await RequiredAsync(async () =>
        {
            var current = await ReadOwnedAsync(ct).ConfigureAwait(false);
            var next = InvalidateObsoleteQueuedRepairs(change(current));
            if (ReferenceEquals(next, current))
            {
                _state = current;
                return true;
            }

            next = Retain(next) with { Revision = checked(current.Revision + 1) };
            if (!await store.CompareExchangeAsync(next, current.Revision, _owner, ct).ConfigureAwait(false))
                return false;
            _state = next;
            return true;
        }, ct).ConfigureAwait(false);
    }

    OperationalProcessorState Retain(OperationalProcessorState state)
    {
        var cutoff = clock.GetUtcNow() - options.CompletedRecordRetention;
        var plans = state.Plans.Where(p => p.ExpiresAt >= cutoff || state.Repairs.Any(r =>
                                                                     r.PlanId == p.Id &&
                                                                     r.Status is OperationalRepairStatuses.Queued
                                                                         or OperationalRepairStatuses.Running
                                                                         or OperationalRepairStatuses.Indeterminate)
                                                                 || state.Block?.AttemptId == p.FailedAttemptId)
            .ToArray();
        var referenced = plans.Select(p => p.FailedAttemptId).Append(state.Block?.AttemptId ?? Guid.Empty).ToHashSet();
        var resolved = state.Plans.Where(p =>
                state.Repairs.Any(r => r.PlanId == p.Id && r.Status is
                    OperationalRepairStatuses.Committed or OperationalRepairStatuses.Resumed))
            .Select(p => p.FailedAttemptId).ToHashSet();
        var attempts = state.Attempts.Where(a => a.CompletedAt is null || a.CompletedAt >= cutoff
                                                                       || referenced.Contains(a.Id) ||
                                                                       a.Outcome == OperationalAttemptOutcomes
                                                                           .Indeterminate
                                                                       || a.Outcome is OperationalAttemptOutcomes.Failed
                                                                           or OperationalAttemptOutcomes.Interrupted &&
                                                                       !resolved.Contains(a.Id) &&
                                                                       !state.Attempts.Any(success =>
                                                                           success.Outcome == OperationalAttemptOutcomes
                                                                               .Committed &&
                                                                           success.StartedAt >= a.CompletedAt
                                                                           && success.ExpectedCheckpoint ==
                                                                           a.ExpectedCheckpoint
                                                                           && (a.Sources.Count == 0 ||
                                                                               success.Sources
                                                                                   .SequenceEqual(a.Sources))))
            .ToArray();
        return state with
        {
            Plans = plans,
            Attempts = attempts,
            Approvals = state.Approvals.Where(a => plans.Any(p => p.Id == a.PlanId)).ToArray(),
            Repairs = state.Repairs.Where(r => plans.Any(p => p.Id == r.PlanId)).ToArray(),
            RetainedSince = attempts.Length < state.Attempts.Count ? cutoff : state.RetainedSince
        };
    }

    async ValueTask RequiredAsync(Func<ValueTask<bool>> operation, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await operation().ConfigureAwait(false))
                    return;
            }
            catch (Exception ex) when (ex is not (OperationalOwnershipException
                                           or OperationalCheckpointConflictException or OperationCanceledException
                                           or ArgumentException or InvalidOperationException
                                           or UnauthorizedAccessException or InvalidDataException))
            {
                // Required journal outages pause invocation. Keep the exact pending write in memory;
                // termination leaves the durable start visible for automatic crash replay.
            }

            await Task.Delay(options.JournalRetryDelay, clock, ct).ConfigureAwait(false);
        }
    }
}

sealed class OperationalOwnershipException() : InvalidOperationException("Operational workload ownership changed.");

sealed class OperationalCheckpointConflictException()
    : ProjectionConcurrencyException("The authoritative checkpoint changed before operational completion.");

sealed class OperationalExecution
{
    internal OperationalSession? Session { get; set; }
}

sealed class OperationalTraceBuffer(int limit)
{
    readonly List<OperationalTraceEntry> _entries = [];
    internal bool Limited { get; private set; }

    internal void Record(string kind, string component, string? identity, string? execution, string outcome)
    {
        lock (_entries)
        {
            if (_entries.Count < limit)
                _entries.Add(new OperationalTraceEntry(kind, component, identity, execution, outcome));
            else
                Limited = true;
        }
    }

    internal OperationalTraceEntry[] Snapshot()
    {
        lock (_entries)
            return _entries.ToArray();
    }
}
