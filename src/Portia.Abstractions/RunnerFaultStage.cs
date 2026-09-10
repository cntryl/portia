namespace Cntryl.Portia;

/// <summary>
///     The phase of a background runner's work that faulted. This is the complete, closed vocabulary
///     behind the <c>Stage</c> field of Portia's runner-fault log; it is deliberately an enum rather
///     than free text so a caller cannot widen the set, and so the stage a fault reports is decided
///     where the fault is caught rather than inferred from the wording of a message.
/// </summary>
public enum RunnerFaultStage
{
    /// <summary>The runner's own work faulted, with no more specific phase.</summary>
    Execution = 0,

    /// <summary>Obtaining membership, a lease, or another right to run faulted.</summary>
    Acquisition = 1,

    /// <summary>Renewing an already-held reservation or lease faulted.</summary>
    Renewal = 2,

    /// <summary>Observing a durable source for changes faulted or ended unexpectedly.</summary>
    Watch = 3,

    /// <summary>Validating an inbound actor or credential faulted.</summary>
    Validation = 4,

    /// <summary>Delivering or consuming a notification faulted.</summary>
    Notification = 5,

    /// <summary>A hosted tenant or partition workload faulted or ended unexpectedly.</summary>
    Workload = 6,

    /// <summary>Releasing, cancelling, or otherwise winding down faulted.</summary>
    Cleanup = 7
}
