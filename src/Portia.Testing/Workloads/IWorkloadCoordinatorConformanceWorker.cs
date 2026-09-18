namespace Cntryl.Portia.Testing;

/// <summary>Owns one coordinator worker and the resources backing its independent connection.</summary>
public interface IWorkloadCoordinatorConformanceWorker : IAsyncDisposable
{
    /// <summary>Gets the coordinator exercised by the conformance suite.</summary>
    IWorkloadCoordinator Coordinator { get; }
}
