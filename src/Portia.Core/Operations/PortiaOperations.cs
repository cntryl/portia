using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Enforces scoped policy and immutable recovery plans over a durable operational store.</summary>
public sealed class PortiaOperations : IPortiaOperations
{
    readonly IOperationalStore _store;
    readonly OperationalOptions _options;
    readonly IApplicationTopology _topology;
    readonly IOperationalAuthorizer? _authorizer;
    readonly TimeProvider _clock;

    /// <summary>Creates the operational API. An absent authorizer denies every operation.</summary>
    public PortiaOperations(IOperationalStore store, OperationalOptions options, IApplicationTopology topology,
        IOperationalAuthorizer? authorizer = null, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _authorizer = authorizer;
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<ApplicationTopology> DescribeApplicationAsync(ClaimsPrincipal actor,
        OperationalScope scope, CancellationToken ct = default)
    {
        await AuthorizeAsync(actor, scope, OperationalAccess.Read, ct).ConfigureAwait(false);
        return _topology.Describe();
    }

    /// <inheritdoc />
    public async ValueTask<OperationalHealth> GetSystemHealthAsync(ClaimsPrincipal actor,
        OperationalScope scope, CancellationToken ct = default)
    {
        await AuthorizeAsync(actor, scope, OperationalAccess.Read, ct).ConfigureAwait(false);
        var states = await _store.ListAsync(scope, ct).ConfigureAwait(false);
        return new OperationalHealth(states.Count, states.Count(s => s.Block is not null),
            states.Count(s => s.Attempts.Any(a => a.Outcome == "running")),
            states.Count == 0 ? null : states.Min(s => s.RetainedSince));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OperationalProcessorState>> ListIssuesAsync(ClaimsPrincipal actor,
        OperationalScope scope, CancellationToken ct = default)
    {
        await AuthorizeAsync(actor, scope, OperationalAccess.Read, ct).ConfigureAwait(false);
        return (await _store.ListAsync(scope, ct).ConfigureAwait(false)).Where(s => s.Block is not null).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<OperationalProcessorState?> InspectProcessorAsync(ClaimsPrincipal actor,
        OperationalTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await AuthorizeAsync(actor, target.Scope, OperationalAccess.Read, ct).ConfigureAwait(false);
        _ = target.Identity;
        return await _store.ReadAsync(target, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<OperationalAttempt> TraceCausalityAsync(ClaimsPrincipal actor, OperationalTarget target,
        Guid attemptId, CancellationToken ct = default)
    {
        var state = await InspectProcessorAsync(actor, target, ct).ConfigureAwait(false);
        return state?.Attempts.SingleOrDefault(a => a.Id == attemptId)
            ?? throw new KeyNotFoundException("The attempt is outside retained history or does not exist in this scope.");
    }

    /// <inheritdoc />
    public async ValueTask<RepairPlan> PreviewRepairAsync(ClaimsPrincipal actor, OperationalTarget target,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await AuthorizeAsync(actor, target.Scope, OperationalAccess.Preview, ct).ConfigureAwait(false);
        var id = Guid.NewGuid();
        var now = _clock.GetUtcNow();
        var saved = await ChangeAsync(target, state =>
        {
            var block = state.Block ?? throw new InvalidOperationException("The processor is not blocked.");
            var attempt = state.Attempts.Single(a => a.Id == block.AttemptId);
            var action = attempt.Outcome == "committed_cleanup_failed" || state.Checkpoint != attempt.ExpectedCheckpoint
                ? "resume" : "retry";
            if (action == "retry" && attempt.Sources.Count == 0)
                action = "resume";
            var plan = new RepairPlan(id, target, state.OwnerSession, block.AttemptId, block.FailureRevision,
                action, state.Checkpoint, attempt.Sources.ToArray(), state.Settings, now + _options.RepairPlanLifetime);
            return state with { Plans = [.. state.Plans, plan] };
        }, ct).ConfigureAwait(false);
        return saved.Plans.Single(p => p.Id == id);
    }

    /// <inheritdoc />
    public async ValueTask<RepairApproval> ApproveRepairAsync(ClaimsPrincipal actor, OperationalTarget target,
        Guid planId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await AuthorizeAsync(actor, target.Scope, OperationalAccess.Approve, ct).ConfigureAwait(false);
        var actorReference = Attribution(actor);
        var saved = await ChangeAsync(target, state =>
        {
            var plan = RequireCurrentPlan(state, planId, _clock.GetUtcNow());
            if (state.Repairs.Any(r => r.PlanId == planId))
                throw new InvalidOperationException("An execution has already been requested for this plan.");
            var approval = new RepairApproval(planId, actorReference, _clock.GetUtcNow(), plan.ExpiresAt);
            return state with { Approvals = [.. state.Approvals.Where(a => a.PlanId != planId), approval] };
        }, ct).ConfigureAwait(false);
        return saved.Approvals.Single(a => a.PlanId == planId);
    }

    /// <inheritdoc />
    public async ValueTask RevokeRepairAsync(ClaimsPrincipal actor, OperationalTarget target, Guid planId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await AuthorizeAsync(actor, target.Scope, OperationalAccess.Approve, ct).ConfigureAwait(false);
        _ = await ChangeAsync(target, state =>
        {
            if (state.Repairs.Any(r => r.PlanId == planId && r.Status != "queued"))
                throw new InvalidOperationException("The worker already claimed this repair.");
            if (!state.Approvals.Any(a => a.PlanId == planId))
                throw new KeyNotFoundException("No approval exists for this plan.");
            return state with
            {
                Approvals = state.Approvals.Select(a => a.PlanId == planId ? a with { Revoked = true } : a).ToArray(),
                Repairs = state.Repairs.Select(r => r.PlanId == planId ? r with { Status = "invalidated" } : r).ToArray()
            };
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<OperationalRepair> ExecuteRepairAsync(ClaimsPrincipal actor, OperationalTarget target,
        Guid planId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await AuthorizeAsync(actor, target.Scope, OperationalAccess.Execute, ct).ConfigureAwait(false);
        var actorReference = Attribution(actor);
        var id = Guid.NewGuid();
        var saved = await ChangeAsync(target, state =>
        {
            // An authorized duplicate observes the original outcome even after expiry or progress.
            if (state.Repairs.Any(r => r.PlanId == planId))
                return state;
            _ = RequireCurrentPlan(state, planId, _clock.GetUtcNow());
            RequireApproval(state, planId, _clock.GetUtcNow());
            return state with
            {
                Repairs = [.. state.Repairs, new OperationalRepair(id, planId, actorReference,
                "queued", _clock.GetUtcNow())]
            };
        }, ct).ConfigureAwait(false);
        return saved.Repairs.Single(r => r.PlanId == planId);
    }

    /// <inheritdoc />
    public async ValueTask<OperationalRepair> VerifyRepairAsync(ClaimsPrincipal actor, OperationalTarget target,
        Guid planId, CancellationToken ct = default)
    {
        var state = await InspectProcessorAsync(actor, target, ct).ConfigureAwait(false);
        return state?.Repairs.SingleOrDefault(r => r.PlanId == planId)
            ?? throw new KeyNotFoundException("No execution exists for this plan in retained history.");
    }

    internal static RepairPlan RequireCurrentPlan(OperationalProcessorState state, Guid planId, DateTimeOffset now)
    {
        var plan = state.Plans.SingleOrDefault(p => p.Id == planId)
            ?? throw new KeyNotFoundException("The repair plan does not exist in retained history.");
        if (plan.Target != state.Target || plan.OwnerSession != state.OwnerSession
            || plan.ExpiresAt <= now || state.Block is not { } block
            || block.AttemptId != plan.FailedAttemptId || block.FailureRevision != plan.FailureRevision
            || state.Settings != plan.Settings || state.Checkpoint != plan.ExpectedCheckpoint
            || !state.Attempts.Single(a => a.Id == plan.FailedAttemptId).Sources.SequenceEqual(plan.Sources))
            throw new InvalidOperationException("The repair plan is expired or no longer matches this workload.");
        return plan;
    }

    internal static void RequireApproval(OperationalProcessorState state, Guid planId, DateTimeOffset now)
    {
        if (!state.Approvals.Any(a => a.PlanId == planId && !a.Revoked && a.ExpiresAt > now))
            throw new UnauthorizedAccessException("An unexpired operator approval of this exact plan is required.");
    }

    async ValueTask AuthorizeAsync(ClaimsPrincipal actor, OperationalScope scope, OperationalAccess permission,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        if (!actor.Identities.Any(identity => identity.IsAuthenticated) || _authorizer is null
            || !await _authorizer.AuthorizeAsync(actor, scope, permission, ct).ConfigureAwait(false))
            throw new UnauthorizedAccessException("The actor is not authorized for this scoped operational action.");
    }

    static ActorAttribution Attribution(ClaimsPrincipal actor)
    {
        try
        { return ActorAttribution.FromPrincipal(actor); }
        catch (InvalidOperationException)
        {
            throw new UnauthorizedAccessException("A stable authenticated subject and issuer are required.");
        }
    }

    async ValueTask<OperationalProcessorState> ChangeAsync(OperationalTarget target,
        Func<OperationalProcessorState, OperationalProcessorState> change, CancellationToken ct)
    {
        _ = target.Identity;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var current = await _store.ReadAsync(target, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The processor has no durable operational evidence.");
            var next = change(current);
            if (ReferenceEquals(next, current))
                return current;
            next = next with { Revision = checked(current.Revision + 1) };
            if (await _store.CompareExchangeAsync(next, current.Revision, current.OwnerSession, ct).ConfigureAwait(false))
                return next;
        }
    }
}
