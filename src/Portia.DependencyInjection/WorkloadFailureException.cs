namespace Cntryl.Portia;

/// <summary>
///     Thrown when a hosted projector or reactor reaches its configured consecutive-failure limit.
///     The checkpoint is left unchanged and the worker faults rather than silently retrying the same
///     event forever.
/// </summary>
/// <param name="identity">The workload that exhausted its attempts.</param>
/// <param name="attempts">The number of consecutive failed passes.</param>
/// <param name="innerException">The final pass failure.</param>
public sealed class WorkloadFailureException(WorkloadIdentity identity, int attempts, Exception innerException)
    : Exception($"Workload '{identity}' failed {attempts} consecutive attempts.", innerException)
{
    /// <summary>Gets the workload that exhausted its attempts.</summary>
    public WorkloadIdentity Identity { get; } = identity;

    /// <summary>Gets the number of consecutive failed passes.</summary>
    public int Attempts { get; } = attempts;
}
