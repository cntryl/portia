using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>An exact tenant or the application's global processing scope.</summary>
public sealed record OperationalScope(string? Tenant)
{
    /// <summary>Gets the global scope, which never includes tenant workloads.</summary>
    public static OperationalScope Global { get; } = new((string?)null);

    /// <summary>Creates an exact tenant scope.</summary>
    public static OperationalScope ForTenant(TenantId tenant) => new(tenant.Value);

    internal void Validate()
    {
        if (Tenant is not null)
            _ = new TenantId(Tenant);
    }
}

/// <summary>Identifies a processor, canonical source pattern, generation, and exact access scope.</summary>
public sealed record OperationalTarget(string Processor, string Pattern, OperationalScope Scope,
    string? RebuildId = null)
{
    internal CheckpointIdentity Identity
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Processor);
            ArgumentNullException.ThrowIfNull(Scope);
            Scope.Validate();
            var parts = Pattern?.StartsWith("stream://", StringComparison.Ordinal) == true
                ? Pattern[9..].Split('/') : [];
            if (parts.Length != 3)
                throw new ArgumentException("An operational target requires a canonical bound stream pattern.");
            var pattern = EventStreamPattern.ForPattern(parts[0], parts[1] == "*" ? null : parts[1],
                parts[2] == "*" ? null : parts[2]);
            if (Scope.Tenant is { } tenant && pattern.Realm != tenant)
                throw new ArgumentException("The target pattern must belong to its exact tenant scope.");
            return new CheckpointIdentity(Processor, pattern, RebuildId);
        }
    }
}

/// <summary>Host-owned permissions; observation confers no administrative or business authority.</summary>
public enum OperationalAccess
{
    /// <summary>Read descriptions and durable execution evidence.</summary>
    Read,
    /// <summary>Preview an immutable recovery plan.</summary>
    Preview,
    /// <summary>Approve or revoke an exact recovery plan using an operator credential.</summary>
    Approve,
    /// <summary>Queue an approved recovery for its owning worker.</summary>
    Execute
}

/// <summary>Authorizes every operation against its explicit scope. No implementation means deny.</summary>
public interface IOperationalAuthorizer
{
    /// <summary>Evaluates a host policy without granting downstream command permissions.</summary>
    ValueTask<bool> AuthorizeAsync(ClaimsPrincipal actor, OperationalScope scope,
        OperationalAccess permission, CancellationToken ct = default);
}

/// <summary>Durable processor diagnosis and operator-approved recovery.</summary>
public interface IPortiaOperations
{
    /// <summary>Describes registrations without constructing application dependencies.</summary>
    ValueTask<ApplicationTopology> DescribeApplicationAsync(ClaimsPrincipal actor, OperationalScope scope, CancellationToken ct = default);
    /// <summary>Reports processing health for an exact scope.</summary>
    ValueTask<OperationalHealth> GetSystemHealthAsync(ClaimsPrincipal actor, OperationalScope scope, CancellationToken ct = default);
    /// <summary>Lists blocked workloads in an exact scope.</summary>
    ValueTask<IReadOnlyList<OperationalProcessorState>> ListIssuesAsync(ClaimsPrincipal actor, OperationalScope scope, CancellationToken ct = default);
    /// <summary>Reads one workload's durable evidence.</summary>
    ValueTask<OperationalProcessorState?> InspectProcessorAsync(ClaimsPrincipal actor, OperationalTarget target, CancellationToken ct = default);
    /// <summary>Reads a recorded attempt and its bounded, local causal evidence.</summary>
    ValueTask<OperationalAttempt> TraceCausalityAsync(ClaimsPrincipal actor, OperationalTarget target, Guid attemptId, CancellationToken ct = default);
    /// <summary>Persists an immutable plan without invoking application code.</summary>
    ValueTask<RepairPlan> PreviewRepairAsync(ClaimsPrincipal actor, OperationalTarget target, CancellationToken ct = default);
    /// <summary>Approves an exact plan; hosts must exclude this permission from agent credentials.</summary>
    ValueTask<RepairApproval> ApproveRepairAsync(ClaimsPrincipal actor, OperationalTarget target, Guid planId, CancellationToken ct = default);
    /// <summary>Revokes an approval before the owning worker claims execution.</summary>
    ValueTask RevokeRepairAsync(ClaimsPrincipal actor, OperationalTarget target, Guid planId, CancellationToken ct = default);
    /// <summary>Reauthorizes and queues one execution per plan; duplicates return that execution.</summary>
    ValueTask<OperationalRepair> ExecuteRepairAsync(ClaimsPrincipal actor, OperationalTarget target, Guid planId, CancellationToken ct = default);
    /// <summary>Reports the approved unit's processing outcome, without a business consistency verdict.</summary>
    ValueTask<OperationalRepair> VerifyRepairAsync(ClaimsPrincipal actor, OperationalTarget target, Guid planId, CancellationToken ct = default);
}

/// <summary>Durable workload documents with conditional, synchronously persisted state transitions.</summary>
public interface IOperationalStore
{
    /// <summary>Reads one document, or null before the workload is first owned.</summary>
    ValueTask<OperationalProcessorState?> ReadAsync(OperationalTarget target, CancellationToken ct = default);
    /// <summary>Lists only documents in the supplied exact scope.</summary>
    ValueTask<IReadOnlyList<OperationalProcessorState>> ListAsync(OperationalScope scope, CancellationToken ct = default);
    /// <summary>
    /// Replaces a document only when revision and owner match. Revision zero and an empty owner
    /// require absence. The replacement revision must be exactly one greater.
    /// </summary>
    /// <exception cref="ProjectionConcurrencyException">A competing transaction prevented the write; reload before retrying.</exception>
    ValueTask<bool> CompareExchangeAsync(OperationalProcessorState replacement, long expectedRevision,
        Guid expectedOwner, CancellationToken ct = default);
}

/// <summary>Atomically commits a reactor completion receipt, repair outcome, and existing checkpoint key.</summary>
public interface IOperationalReactionCheckpointStore : IProjectionCheckpointStore
{
    /// <summary>Commits only when checkpoint, owner, running attempt, and document revision still match.</summary>
    ValueTask<bool> CommitAsync(OperationalProcessorState replacement, long expectedRevision, Guid ownerSession,
        Guid attemptId, string? expectedCheckpoint, string? nextCheckpoint, CancellationToken ct = default);
}

/// <summary>Describes only the application's selected registrations.</summary>
public interface IApplicationTopology
{
    /// <summary>Returns a snapshot without resolving handlers or processors.</summary>
    ApplicationTopology Describe();
}

/// <summary>Registered requests and processors; opaque application dependencies remain unknown.</summary>
public sealed record ApplicationTopology(IReadOnlyList<RequestTopology> Requests,
    IReadOnlyList<ProcessorTopology> Processors, string Coverage);

/// <summary>A generated request-to-handler registration.</summary>
public sealed record RequestTopology(string Request, string Handler);

/// <summary>A selected processor and the ordered event handlers used by its generated dispatcher.</summary>
public sealed record ProcessorTopology(string Name, string Component, string Kind, string Scope,
    IReadOnlyList<string> Subscriptions, string Coverage);

/// <summary>Scoped health counts and the earliest retained evidence.</summary>
public sealed record OperationalHealth(int Processors, int Blocked, int Running, DateTimeOffset? RetainedSince);

/// <summary>Source identities and opaque reader positions, with no event payload.</summary>
public sealed record OperationalSource(string Stream, string EventId, ulong ResourceOffset, string? NextCursor)
{
    internal static OperationalSource From(DomainEventRecord record) => new(record.Stream.ToString(),
        record.Event.Metadata.EventId.ToString(), record.ResourceOffset, record.NextCursor.Value);
}

/// <summary>Processing settings bound into plans and checked again by the owner.</summary>
public sealed record OperationalProcessingSettings(int MaxBatchSize, int MaxEventsPerPass,
    int FailureAttemptLimit, long PollIntervalTicks, long MaximumFailureDelayTicks);

/// <summary>A bounded metadata-only local observation; it conveys no authorization.</summary>
public sealed record OperationalTraceEntry(string Kind, string Component, string? Identity,
    string? ExecutionId, string Outcome);

/// <summary>An attempt's durable source set, actual invocations, and outcome.</summary>
public sealed record OperationalAttempt(Guid Id, Guid OwnerSession, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, string Outcome, string? ExpectedCheckpoint, string? NextCheckpoint,
    IReadOnlyList<OperationalSource> Sources, IReadOnlyList<OperationalTraceEntry> Trace,
    string Coverage, ApplicationTopology Topology, string? FailureStage = null, string? FailureKind = null,
    bool? IsTransient = null);

/// <summary>A terminal processing failure, preserved until explicit recovery.</summary>
public sealed record OperationalBlock(Guid AttemptId, long FailureRevision);

/// <summary>An immutable, five-minute recovery proposal tied to an owner and exact failed unit.</summary>
public sealed record RepairPlan(Guid Id, OperationalTarget Target, Guid OwnerSession,
    Guid FailedAttemptId, long FailureRevision, string Action, string? ExpectedCheckpoint,
    IReadOnlyList<OperationalSource> Sources, OperationalProcessingSettings Settings, DateTimeOffset ExpiresAt);

/// <summary>An operator's approval of the exact persisted plan.</summary>
public sealed record RepairApproval(Guid PlanId, ActorAttribution Actor, DateTimeOffset ApprovedAt,
    DateTimeOffset ExpiresAt, bool Revoked = false);

/// <summary>One worker-owned execution request and its processing outcome.</summary>
public sealed record OperationalRepair(Guid Id, Guid PlanId, ActorAttribution RequestedBy, string Status,
    DateTimeOffset RequestedAt, Guid? AttemptId = null, string? Checkpoint = null);

/// <summary>A revisioned workload journal. Stores return detached snapshots.</summary>
public sealed record OperationalProcessorState(OperationalTarget Target, string Kind, Guid OwnerSession,
    long Revision, string? Checkpoint, OperationalProcessingSettings Settings,
    IReadOnlyList<OperationalAttempt> Attempts, OperationalBlock? Block,
    IReadOnlyList<RepairPlan> Plans, IReadOnlyList<RepairApproval> Approvals,
    IReadOnlyList<OperationalRepair> Repairs, int ConsecutiveFailures, DateTimeOffset RetainedSince);
