namespace Cntryl.Portia;

/// <summary>The states of an <see cref="OperationalRepair" />.</summary>
/// <remarks>These are the exact strings stored in the journal and returned to operators.</remarks>
public static class OperationalRepairStatuses
{
    /// <summary>The repair is approved and waiting for the owning worker.</summary>
    public const string Queued = "queued";

    /// <summary>The owning worker has claimed the repair.</summary>
    public const string Running = "running";

    /// <summary>The repair no longer applies to the current state of the processor.</summary>
    public const string Invalidated = "invalidated";

    /// <summary>The worker stopped while the repair was running.</summary>
    public const string Indeterminate = "indeterminate";

    /// <summary>The repair completed and progress was committed.</summary>
    public const string Committed = "committed";

    /// <summary>A source read succeeded and found no further work at the confirmed checkpoint.</summary>
    public const string Resumed = "resumed";

    /// <summary>The repair ran and failed.</summary>
    public const string Failed = "failed";
}
