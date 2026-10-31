namespace Cntryl.Portia;

// A session exists only inside one coordinator ownership callback. All durable changes are fenced
// by that session. Each write reloads the document so concurrent operator approval cannot be lost.
sealed class OperationalSession(IOperationalStore store, OperationalOptions options,
    IApplicationTopology topology, TimeProvider clock)
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
        if (_state.ConsecutiveFailures != 0)
            await ChangeAsync(state => state with { ConsecutiveFailures = 0 }, ct).ConfigureAwait(false);
    }
    internal RepairPlan? ActivePlan => _repairPlan is { } id ? _state.Plans.Single(p => p.Id == id) : null;
    internal IReadOnlyList<OperationalSource>? InterruptedSources { get; private set; }

    internal void EnsureRecoveryCompleted(ProjectionCheckpoint checkpoint)
    {
        if (InterruptedSources is { Count: > 0 }
            || _repairPlan is { } id && _state.Repairs.Any(r => r.PlanId == id && r.Status == "running"))
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
            InterruptedSources = prior?.Attempts.LastOrDefault(a => a.Outcome == "running"
                && (checkpoint is null || a.ExpectedCheckpoint == checkpoint.Value.Cursor.Value))?.Sources;
            var now = clock.GetUtcNow();
            var interruptedRepair = prior?.Repairs.Any(r => r.Status == "running") == true;
            var next = prior is null
                ? new OperationalProcessorState(target, kind, _owner, 1, checkpoint?.Cursor.Value, settings,
                    [], null, [], [], [], 0, now)
                : prior with
                {
                    OwnerSession = _owner,
                    Revision = checked(prior.Revision + 1),
                    Settings = settings,
                    Checkpoint = checkpoint?.Cursor.Value ?? prior.Checkpoint,
                    Attempts = prior.Attempts.Select(a => a.Outcome == "running"
                        ? a with
                        {
                            Outcome = kind == "projector" && checkpoint is { } native && a.ExpectedCheckpoint != native.Cursor.Value ? "indeterminate" : "interrupted",
                            CompletedAt = now,
                            Coverage = a.Coverage + "; process interrupted before confirmation"
                        } : a).ToArray(),
                    Repairs = prior.Repairs.Select(r => r.Status is "queued" or "running"
                        ? r with { Status = r.Status == "running" ? "indeterminate" : "invalidated" } : r).ToArray(),
                    Block = interruptedRepair ? null : prior.Block
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
                Attempts = state.Attempts.Select(a => a.Outcome is "running" or "failed" or "interrupted"
                    && a.ExpectedCheckpoint != checkpoint.Cursor.Value
                    ? a with
                    {
                        Outcome = "indeterminate",
                        CompletedAt = a.CompletedAt ?? now,
                        Coverage = a.Coverage + "; authoritative progress differs from historical confirmation"
                    } : a).ToArray()
            };
        }, ct).ConfigureAwait(false);
        if (_repairPlan is { } active && !_state.Repairs.Any(r => r.PlanId == active && r.Status == "running"))
            _repairPlan = null;
        if (_repairPlan is { } running && !_state.Approvals.Any(a => a.PlanId == running
            && !a.Revoked && a.ExpiresAt > clock.GetUtcNow()))
        {
            await ChangeAsync(state => CompleteRepair(state, "invalidated", null, state.Checkpoint), ct).ConfigureAwait(false);
            _repairPlan = null;
            return false;
        }
        if (_state.Block is null)
        {
            if (_state.ConsecutiveFailures > 0 && _state.Attempts.Count > 0 && _state.Attempts[^1] is { Outcome: "failed", CompletedAt: { } ended })
            {
                var settings = _state.Settings;
                var maximum = settings.MaximumFailureDelayTicks;
                var initial = Math.Min(settings.PollIntervalTicks, maximum);
                var exponent = Math.Min(_state.ConsecutiveFailures - 1, 30);
                var ticks = initial > maximum >> exponent ? maximum : initial << exponent;
                if (clock.GetUtcNow() < ended + TimeSpan.FromTicks(Math.Min(ticks, maximum)))
                    return false;
            }
            return true;
        }
        if (_repairPlan is not null)
            return true;
        var queued = _state.Repairs.FirstOrDefault(r => r.Status == "queued");
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
                    ? r with { Status = "invalidated" } : r).ToArray()
                };
            }
            return state with
            {
                ConsecutiveFailures = 0,
                Repairs = state.Repairs.Select(r => r.Id == queued.Id
                ? r with { Status = "running" } : r).ToArray()
            };
        }, ct).ConfigureAwait(false);
        if (!_state.Repairs.Any(r => r.Id == queued.Id && r.Status == "running"))
            return false;
        _repairPlan = queued.PlanId;
        if (claimed!.Action == "resume")
        {
            await ChangeAsync(state => CompleteRepair(state with { Block = null, ConsecutiveFailures = 0 },
                state.Attempts.Single(a => a.Id == claimed.FailedAttemptId).Outcome == "indeterminate" ? "indeterminate" : "committed",
                null, checkpoint.Cursor.Value), ct).ConfigureAwait(false);
            _repairPlan = null;
        }
        return true;
    }

    internal ProjectionRunOptions BoundOptions(ProjectionRunOptions normal)
    {
        var sources = ActivePlan?.Sources ?? InterruptedSources;
        return sources is { Count: > 0 }
            ? normal with { MaxBatchSize = sources.Count, MaxEventsPerPass = sources.Count } : normal;
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
            var attempt = new OperationalAttempt(id, _owner, started, null, "running", checkpoint.Cursor.Value,
                sources[^1].NextCursor, sources, [], "local awaited generated dispatch; opaque dependencies and detached work unknown",
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
            if (state.Attempts.Single(a => a.Id == id).Outcome == "committed")
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
        InterruptedSources = null;
    }

    internal async ValueTask ConfirmProjectionAsync(ProjectionCheckpoint next, CancellationToken ct)
    {
        var id = _attempt ?? throw new InvalidOperationException("A durable attempt start is required.");
        await ChangeAsync(state => Succeed(state, id, next.Cursor.Value), ct).ConfigureAwait(false);
        _attempt = null;
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
            if (state.Attempts.Any(a => a.Id == id && a.Outcome != "running"))
                return state;
            var now = clock.GetUtcNow();
            var prior = state.Attempts.SingleOrDefault(a => a.Id == id)
                ?? new OperationalAttempt(id, _owner, now, null, "running", state.Checkpoint, null,
                    ActivePlan?.Sources ?? InterruptedSources ?? [], [], "no handler invocation recorded", topology.Describe());
            var transient = diagnostic?.IsTransient ?? true;
            var failures = checked(state.ConsecutiveFailures + 1);
            var terminal = committed is not null || !transient || failures >= state.Settings.FailureAttemptLimit;
            var attempt = prior with
            {
                CompletedAt = now,
                Outcome = committed is null ? "failed" : "committed_cleanup_failed",
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
            return terminal ? CompleteRepair(next, committed is null ? "failed" : "committed", id,
                committed?.Cursor.Value) : next;
        }, ct).ConfigureAwait(false);
        _attempt = null;
        _failureRecorded = true;
    }

    OperationalProcessorState Succeed(OperationalProcessorState state, Guid id, string? checkpoint)
        => CompleteRepair(state with
        {
            Checkpoint = checkpoint,
            ConsecutiveFailures = _repairPlan is null ? state.ConsecutiveFailures : 0,
            Block = null,
            Attempts = state.Attempts.Select(a => a.Id == id ? a with
            {
                Outcome = "committed",
                CompletedAt = clock.GetUtcNow(),
                NextCheckpoint = checkpoint,
                Trace = TraceSnapshot(),
                Coverage = Coverage(a)
            } : a).ToArray()
        }, "committed", id, checkpoint);

    OperationalProcessorState CompleteRepair(OperationalProcessorState state, string status, Guid? attempt, string? checkpoint)
        => _repairPlan is not { } id ? state : state with
        {
            Repairs = state.Repairs.Select(r => r.PlanId == id
                ? r with { Status = status, AttemptId = attempt, Checkpoint = checkpoint } : r).ToArray()
        };

    OperationalTraceEntry[] TraceSnapshot() => _trace.Snapshot();
    string Coverage(OperationalAttempt attempt) => attempt.Coverage + (_trace.Limited ? "; trace limit reached" : string.Empty);

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
            var next = change(current);
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
            r.PlanId == p.Id && r.Status is "queued" or "running" or "indeterminate")
            || state.Block?.AttemptId == p.FailedAttemptId).ToArray();
        var referenced = plans.Select(p => p.FailedAttemptId).Append(state.Block?.AttemptId ?? Guid.Empty).ToHashSet();
        var resolved = state.Plans.Where(p => state.Repairs.Any(r => r.PlanId == p.Id && r.Status == "committed"))
            .Select(p => p.FailedAttemptId).ToHashSet();
        var attempts = state.Attempts.Where(a => a.CompletedAt is null || a.CompletedAt >= cutoff
            || referenced.Contains(a.Id) || a.Outcome == "indeterminate"
            || a.Outcome is "failed" or "interrupted" && !resolved.Contains(a.Id) && !state.Attempts.Any(success =>
                success.Outcome == "committed" && success.StartedAt >= a.CompletedAt
                && success.ExpectedCheckpoint == a.ExpectedCheckpoint
                && (a.Sources.Count == 0 || success.Sources.SequenceEqual(a.Sources)))).ToArray();
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
            { if (await operation().ConfigureAwait(false)) return; }
            catch (Exception ex) when (ex is not (OperationalOwnershipException or OperationalCheckpointConflictException or OperationCanceledException
                or ArgumentException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException))
            {
                // Required journal outages pause invocation. Keep the exact pending write in memory;
                // termination leaves the durable start visible for automatic crash replay.
            }
            await Task.Delay(options.JournalRetryDelay, clock, ct).ConfigureAwait(false);
        }
    }
}

sealed class OperationalOwnershipException() : InvalidOperationException("Operational workload ownership changed.");

sealed class OperationalCheckpointConflictException() : ProjectionConcurrencyException("The authoritative checkpoint changed before operational completion.");

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
