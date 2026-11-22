using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

sealed class ProcessorStatusHandler(
    IServiceScopeFactory scopes,
    IEnumerable<WorkloadRegistration> registrations,
    ProcessorRuntimeStatusRegistry runtime,
    TimeProvider? timeProvider = null) : IRequestHandler<GetProcessorStatus, ProcessorStatus>
{
    readonly WorkloadRegistration[] _registrations = [.. registrations];
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async ValueTask<Result<ProcessorStatus>> HandleAsync(IRequestContext<GetProcessorStatus> context,
        CancellationToken ct)
    {
        var request = context.Request;
        var registration = _registrations.FirstOrDefault(item => item.Name == request.Processor);
        if (registration is null)
            return RequestError.NotFound("The requested processor is not registered.");

        TenantId? tenant = null;
        if (request.Tenant is { } tenantValue)
        {
            if (!TenantId.TryParse(tenantValue, null, out var parsed))
                return RequestError.Validation("Processor status requires a valid tenant identity.");
            tenant = parsed;
        }

        if (registration.Scope == WorkloadScope.PerTenant && tenant is null ||
            registration.Scope == WorkloadScope.Global && tenant is not null)
            return RequestError.Validation("Processor status must match the registered tenant or global scope.");

        await using var scope = scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var identity = new WorkloadIdentity(registration.Name, tenant);
        provider.GetRequiredService<WorkloadContext>().Initialize(identity, registration.Name);
        registration.Descriptor.Bind(provider, identity, registration.Name);
        var pattern = registration.Descriptor.Pattern(provider);
        var checkpoint = await ObserveAsync(token =>
            registration.Descriptor.LoadCheckpoint(provider, registration.Processing, token), ct).ConfigureAwait(false);

        var reader = provider.GetService<IDomainEventProgressReader>() ??
                     provider.GetService<IDomainEventReader>() as IDomainEventProgressReader;
        EventStreamProgress? progress = null;
        string? sourceError = null;
        if (checkpoint.Error is null)
        {
            var cursor = checkpoint.Value.Cursor;
            progress = new EventStreamProgress(cursor, null, null, null, checkpoint.ObservedAt);
            if (reader is not null)
            {
                var source = await ObserveAsync(async token =>
                {
                    var observation = await reader.GetProgressAsync(pattern, cursor, token).ConfigureAwait(false);
                    if (observation.Position != cursor)
                        throw new InvalidOperationException("Source progress must preserve the supplied committed checkpoint.");
                    return observation;
                }, ct).ConfigureAwait(false);

                sourceError = source.Error;
                progress = source.Value ?? progress with { ObservedAt = source.ObservedAt };
            }
        }

        Observation<OperationalProcessorState?>? journalObservation = null;
        if (provider.GetService<IOperationalStore>() is { } journal)
        {
            var target = new OperationalTarget(registration.Name, pattern.ToString(),
                new OperationalScope(tenant?.Value), registration.Processing.RebuildId);
            journalObservation = await ObserveAsync(token => journal.ReadAsync(target, token), ct)
                .ConfigureAwait(false);
        }

        var live = runtime.Observe(identity);
        var now = _clock.GetUtcNow();
        var owner = new ProcessorOwnerStatus(live is null ? null : true, now,
            live is null ? "unavailable" : "local_worker");
        if (provider.GetService<IProcessorOwnerProbe>() is { } ownerProbe)
        {
            var sampled = await ObserveAsync(token => ownerProbe.ObserveAsync(identity, token), ct)
                .ConfigureAwait(false);
            owner = sampled.Value ?? new ProcessorOwnerStatus(null, sampled.ObservedAt, "provider", sampled.Error);
            // A local callback is useful only while the provider has not observed its withdrawal.
            live = owner.HasOwner == false ? null : runtime.Observe(identity);
        }

        var state = journalObservation?.Value;
        var blockedAttempt = state?.Block is { } block
            ? state.Attempts.LastOrDefault(item => item.Id == block.AttemptId)
            : null;
        var latest = state is { Attempts.Count: > 0 } ? state.Attempts[^1] : null;
        var attempt = latest is not null && (progress is null ||
            latest.ExpectedCheckpoint == progress.Position.Value ||
            latest.NextCheckpoint == progress.Position.Value && latest.Outcome is
                OperationalAttemptOutcomes.Committed or OperationalAttemptOutcomes.CommittedCleanupFailed)
            ? latest : blockedAttempt;
        var failureAttempt = blockedAttempt ?? (state?.ConsecutiveFailures > 0 &&
            (progress is null || state.Checkpoint == progress.Position.Value)
                ? state.Attempts.LastOrDefault(item => item.Outcome is
                    OperationalAttemptOutcomes.Failed or OperationalAttemptOutcomes.Indeterminate)
                : null);
        var failure = failureAttempt is null ? null : new ProcessorStatusFailure(failureAttempt.FailureStage,
            failureAttempt.FailureKind, failureAttempt.IsTransient, state!.ConsecutiveFailures,
            blockedAttempt is not null, failureAttempt.CompletedAt);
        var repair = Repair(state, blockedAttempt, progress);
        var execution = Execution(registration.FailureAttemptLimit, live, owner, progress, attempt, failure,
            repair, checkpoint.Error, sourceError, journalObservation?.Error, _clock.GetUtcNow());
        return new ProcessorStatus(registration.Name, registration.Descriptor.Kind, tenant?.Value,
            pattern.ToString(), registration.Processing.RebuildId, progress, failure, sourceError, journalObservation?.Error)
        {
            CheckpointError = checkpoint.Error,
            CheckpointObservedAt = checkpoint.ObservedAt,
            JournalObservedAt = journalObservation?.ObservedAt,
            Attempt = attempt is null ? null : new ProcessorAttemptStatus(attempt.Id, attempt.OwnerSession,
                attempt.Outcome, attempt.StartedAt, attempt.CompletedAt, attempt.ExpectedCheckpoint, attempt.NextCheckpoint),
            Owner = owner,
            Execution = execution,
            Repair = repair
        };
    }

    static ProcessorRepairStatus? Repair(OperationalProcessorState? state, OperationalAttempt? block,
        EventStreamProgress? progress)
    {
        if (state is null)
            return null;

        var latest = state.Repairs.Count > 0 ? state.Repairs[^1] : null;
        var plan = state.Plans.LastOrDefault(item => item.Id == latest?.PlanId);
        if (block is null || plan?.FailedAttemptId == block.Id &&
            latest?.Status is OperationalRepairStatuses.Queued or OperationalRepairStatuses.Running)
            return latest is null ? null : new ProcessorRepairStatus(latest.Id, plan?.Action, latest.Status);

        var checkpoint = progress?.Position.Value ?? state.Checkpoint;
        var action = block.Sources.Count == 0 || block.ExpectedCheckpoint != checkpoint ||
                     block.Outcome == OperationalAttemptOutcomes.CommittedCleanupFailed
            ? RepairActions.Resume : RepairActions.Retry;
        return new ProcessorRepairStatus(null, action, "approval_required");
    }

    static ProcessorExecutionStatus Execution(int attemptLimit, ProcessorExecutionStatus? live,
        ProcessorOwnerStatus owner, EventStreamProgress? progress, OperationalAttempt? attempt,
        ProcessorStatusFailure? failure, ProcessorRepairStatus? repair, string? checkpointError, string? sourceError,
        string? journalError, DateTimeOffset now)
    {
        if (live?.Phase == "waiting_for_journal")
            return live;
        if (failure?.IsBlocked == true && repair?.Status != OperationalRepairStatuses.Running)
            return new ProcessorExecutionStatus("blocked", failure.FailedAt, failure.Stage, failure.Kind,
                repair?.Status == OperationalRepairStatuses.Queued ? "wait_for_repair" : "preview_and_approve_repair",
                null, failure.ConsecutiveFailures, attemptLimit);
        if (live is not null && live.Phase != "idle")
        {
            return live with
            {
                Phase = live.Phase == "processing" && now - live.Since >= TimeSpan.FromSeconds(30)
                    ? "long_running" : live.Phase
            };
        }

        if (owner.HasOwner is false)
            return new ProcessorExecutionStatus("owner_unavailable", null, null, null, "restore_worker",
                null, null, attemptLimit);
        if (attempt?.Outcome is OperationalAttemptOutcomes.Running or OperationalAttemptOutcomes.Interrupted
            or OperationalAttemptOutcomes.Indeterminate)
            return new ProcessorExecutionStatus("recovery_unconfirmed", attempt.StartedAt, attempt.FailureStage,
                attempt.Outcome, "observe_owner_and_reconcile", null, failure?.ConsecutiveFailures, attemptLimit);
        if ((checkpointError ?? sourceError ?? journalError) is { } error)
            return new ProcessorExecutionStatus("observation_unavailable", null,
                checkpointError is not null ? "checkpoint" : sourceError is not null ? "source" : "journal",
                error, "restore_dependency", null, null, attemptLimit);

        return new ProcessorExecutionStatus(progress?.IsCaughtUp == true ? "caught_up" : live?.Phase ?? "unknown",
            live?.Since, null, null, progress?.IsCaughtUp == true ? "wait_for_source" : "observe_worker",
            null, null, attemptLimit);
    }

    async ValueTask<Observation<T>> ObserveAsync<T>(Func<CancellationToken, ValueTask<T>> read, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(ProcessorStatusTimeouts.Probe, _clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var value = await read(deadline.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new Observation<T>(value, null, _clock.GetUtcNow());
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception exception)
        {
            var reason = exception is ProcessorFailureException { InnerException: { } inner }
                ? inner.GetType().Name : exception.GetType().Name;
            return new Observation<T>(default, timeout.IsCancellationRequested ? "Timeout" : reason, _clock.GetUtcNow());
        }
    }

    readonly record struct Observation<T>(T? Value, string? Error, DateTimeOffset ObservedAt);
}
