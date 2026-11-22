namespace Cntryl.Portia;

/// <summary>Reads committed progress and available failure evidence for one projector or reactor.</summary>
/// <param name="Processor">The component's stable registered workload name.</param>
/// <param name="Tenant">The exact tenant, or null for a global workload.</param>
[Discriminator("portia.processor_status", 1)]
[RequestRoute("*", "*", "*", "status")]
public sealed record GetProcessorStatus(string Processor, string? Tenant = null) : IRequest<ProcessorStatus>, ICallable;

/// <summary>A sampled view of one component's authoritative progress and available failure evidence.</summary>
/// <param name="Processor">The stable registered workload name.</param>
/// <param name="Kind">The component kind: projector or reactor.</param>
/// <param name="Tenant">The exact tenant, or null for a global workload.</param>
/// <param name="Pattern">The bound source stream pattern.</param>
/// <param name="RebuildId">The configured projection generation, or null for live processing.</param>
/// <param name="Progress">Progress sampled from the native checkpoint and source, or null if the checkpoint is unavailable.</param>
/// <param name="Failure">Available failure evidence, or null when no current failure is recorded.</param>
/// <param name="SourceError">The source probe failure classification, or null when sampling succeeded.</param>
/// <param name="FailureError">The failure-evidence read classification, or null when reading succeeded.</param>
public sealed record ProcessorStatus(
    string Processor,
    string Kind,
    string? Tenant,
    string Pattern,
    string? RebuildId,
    EventStreamProgress? Progress,
    ProcessorStatusFailure? Failure,
    string? SourceError = null,
    string? FailureError = null)
{
    /// <summary>Failure classification for the native checkpoint or its configuration.</summary>
    public string? CheckpointError { get; init; }

    /// <summary>Time at which the checkpoint read completed or failed.</summary>
    public DateTimeOffset? CheckpointObservedAt { get; init; }

    /// <summary>Time at which the journal read completed or failed, when configured.</summary>
    public DateTimeOffset? JournalObservedAt { get; init; }

    /// <summary>Point-in-time evidence of workload ownership.</summary>
    public ProcessorOwnerStatus? Owner { get; init; }

    /// <summary>Observed worker phase and available next step.</summary>
    public ProcessorExecutionStatus? Execution { get; init; }

    /// <summary>Metadata about the latest applicable durable attempt.</summary>
    public ProcessorAttemptStatus? Attempt { get; init; }

    /// <summary>The latest relevant repair state or available repair action.</summary>
    public ProcessorRepairStatus? Repair { get; init; }
}

/// <summary>Point-in-time ownership evidence from a local worker or provider.</summary>
/// <param name="HasOwner">True or false when observed, or null when unavailable.</param>
/// <param name="ObservedAt">Time at which this observation was sampled.</param>
/// <param name="Evidence">The source of the observation.</param>
/// <param name="Error">Failure classification if the source could not be read.</param>
public sealed record ProcessorOwnerStatus(bool? HasOwner, DateTimeOffset ObservedAt, string Evidence, string? Error = null);

/// <summary>A sampled execution state. Long-running work is an observation and may still be progressing.</summary>
/// <param name="Phase">The observed phase of the worker or durable recovery.</param>
/// <param name="Since">When the phase was first observed.</param>
/// <param name="Stage">The processing or journal transition, when known.</param>
/// <param name="Reason">The safe failure classification or reason, when known.</param>
/// <param name="NextAction">The next framework action or required operator step, when known.</param>
/// <param name="RetryAt">The earliest next retry time, when applicable.</param>
/// <param name="Failures">Consecutive failures for the active retry, when known.</param>
/// <param name="AttemptLimit">Configured processor failure attempt limit.</param>
public sealed record ProcessorExecutionStatus(
    string Phase, DateTimeOffset? Since, string? Stage, string? Reason, string? NextAction,
    DateTimeOffset? RetryAt, int? Failures, int AttemptLimit);

/// <summary>Historical journal evidence for one attempt, independent of current ownership.</summary>
/// <param name="Id">Durable attempt identity.</param>
/// <param name="OwnerSession">The journal owner session that began the attempt.</param>
/// <param name="Outcome">The recorded outcome of the attempt.</param>
/// <param name="StartedAt">Durable attempt start time.</param>
/// <param name="CompletedAt">Attempt outcome observation time, when available.</param>
/// <param name="ExpectedCheckpoint">The opaque checkpoint at the start of the attempt.</param>
/// <param name="NextCheckpoint">The opaque proposed or confirmed checkpoint, depending on the outcome.</param>
public sealed record ProcessorAttemptStatus(
    Guid Id, Guid OwnerSession, string Outcome, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    string? ExpectedCheckpoint, string? NextCheckpoint);

/// <summary>Observed repair state, or the action available for a currently blocked processor.</summary>
/// <param name="Id">Repair identity if a repair was requested.</param>
/// <param name="Action">The repair action, when known.</param>
/// <param name="Status">The recorded repair status or an indication approval is needed.</param>
public sealed record ProcessorRepairStatus(Guid? Id, string? Action, string Status);

/// <summary>Recorded processing failure evidence; it does not independently establish worker liveness.</summary>
/// <param name="Stage">The stage at which processing failed, when recorded.</param>
/// <param name="Kind">The failure classification, when recorded.</param>
/// <param name="IsTransient">Whether retry can resolve the failure, when known.</param>
/// <param name="ConsecutiveFailures">The recorded consecutive failure count.</param>
/// <param name="IsBlocked">Whether a terminal failure requires recovery.</param>
/// <param name="FailedAt">The recorded failure completion time, when available.</param>
public sealed record ProcessorStatusFailure(
    string? Stage,
    string? Kind,
    bool? IsTransient,
    int ConsecutiveFailures,
    bool IsBlocked,
    DateTimeOffset? FailedAt);
