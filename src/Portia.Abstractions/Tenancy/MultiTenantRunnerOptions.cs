namespace Cntryl.Portia;

/// <summary>Controls tenant-directory recovery and bounded tenant cleanup.</summary>
public sealed class MultiTenantRunnerOptions
{
    /// <summary>Gets or sets the delay before restarting a failed workload or directory read.</summary>
    public TimeSpan RestartInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the shared cleanup grace period during host shutdown.</summary>
    public TimeSpan ShutdownGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the total workload-and-callback cleanup budget for normal tenant removal.</summary>
    public TimeSpan TenantStopTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
