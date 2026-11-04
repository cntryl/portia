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
/// <param name="Progress">Progress sampled from the native checkpoint and source.</param>
/// <param name="Failure">Available failure evidence, or null when no current failure is recorded.</param>
/// <param name="SourceError">The source probe failure classification, or null when sampling succeeded.</param>
/// <param name="FailureError">The failure-evidence read classification, or null when reading succeeded.</param>
public sealed record ProcessorStatus(string Processor, string Kind, string? Tenant, string Pattern,
    string? RebuildId, EventStreamProgress Progress, ProcessorStatusFailure? Failure, string? SourceError = null,
    string? FailureError = null);

/// <summary>Recorded processing failure evidence; it does not independently establish worker liveness.</summary>
/// <param name="Stage">The stage at which processing failed, when recorded.</param>
/// <param name="Kind">The failure classification, when recorded.</param>
/// <param name="IsTransient">Whether retry can resolve the failure, when known.</param>
/// <param name="ConsecutiveFailures">The recorded consecutive failure count.</param>
/// <param name="IsBlocked">Whether a terminal failure requires recovery.</param>
/// <param name="FailedAt">The recorded failure completion time, when available.</param>
public sealed record ProcessorStatusFailure(string? Stage, string? Kind, bool? IsTransient,
    int ConsecutiveFailures, bool IsBlocked, DateTimeOffset? FailedAt);
