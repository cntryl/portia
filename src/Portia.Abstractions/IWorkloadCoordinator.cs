namespace Cntryl.Portia;

/// <summary>Coordinates ownership of the current set of logical workloads across workers.</summary>
public interface IWorkloadCoordinator
{
    /// <summary>Reconciles changing workloads and runs callbacks only while ownership is held.</summary>
    /// <param name="workloads">Returns a complete current snapshot, including retained identities.</param>
    /// <param name="run">
    ///     Runs one identity with an ownership cancellation token.
    ///     The provider cancels and awaits revoked callbacks before releasing ownership.
    /// </param>
    /// <param name="ct">Cancels coordination and every owned callback.</param>
    /// <returns>The complete lifetime, ending only after cancellation or an unrecoverable failure.</returns>
    Task RunAsync(Func<IReadOnlyCollection<WorkloadIdentity>> workloads,
        Func<WorkloadIdentity, CancellationToken, Task> run, CancellationToken ct = default);
}
