namespace Cntryl.Portia;

/// <summary>
///     Thrown when a hosted projector or reactor reports a permanent failure or exhausts its retry budget.
///     The worker faults and retains the pass diagnostic; uncertain commits require authoritative reload.
/// </summary>
/// <param name="identity">The workload that stopped.</param>
/// <param name="attempts">The number of consecutive failed passes.</param>
/// <param name="innerException">The final pass failure.</param>
public sealed class WorkloadFailureException(WorkloadIdentity identity, int attempts, Exception innerException)
    // The identity's tenant stays in the Identity property; the message reaches logs and traces.
    : Exception(innerException is ProcessorFailureException { IsTransient: false }
        ? $"Workload '{identity.Name}' stopped after {attempts} attempts because of a permanent processor failure."
        : $"Workload '{identity.Name}' failed {attempts} consecutive attempts.", innerException)
{
    /// <summary>Gets the workload that exhausted its attempts.</summary>
    public WorkloadIdentity Identity { get; } = identity;

    /// <summary>Gets the number of consecutive failed passes.</summary>
    public int Attempts { get; } = attempts;
}
