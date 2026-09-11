namespace Cntryl.Portia.Testing;

/// <summary>Creates independent workers backed by one isolated coordination namespace.</summary>
public interface IWorkloadCoordinatorConformanceProbe
{
    /// <summary>Gets the maximum time allowed for an ownership change to become observable.</summary>
    TimeSpan ConvergenceTimeout { get; }

    /// <summary>Gets a window covering multiple reconciliations while ownership should remain stable.</summary>
    TimeSpan StabilityWindow { get; }

    /// <summary>Clears all state in the isolated coordination namespace.</summary>
    ValueTask ResetAsync(CancellationToken ct = default);

    /// <summary>Opens an independently disposable worker connected to the shared coordination namespace.</summary>
    ValueTask<IWorkloadCoordinatorConformanceWorker> OpenWorkerAsync(CancellationToken ct = default);
}
