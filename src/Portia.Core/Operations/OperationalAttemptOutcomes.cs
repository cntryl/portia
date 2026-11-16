namespace Cntryl.Portia;

/// <summary>The outcomes recorded on an <see cref="OperationalAttempt" />.</summary>
/// <remarks>These are the exact strings stored in the journal and returned to operators.</remarks>
public static class OperationalAttemptOutcomes
{
    /// <summary>The attempt has durably started and has not settled.</summary>
    public const string Running = "running";

    /// <summary>The attempt failed before anything was committed.</summary>
    public const string Failed = "failed";

    /// <summary>The worker stopped while the attempt was running and nothing is known to have committed.</summary>
    public const string Interrupted = "interrupted";

    /// <summary>Whether the attempt committed cannot be established from the journal alone.</summary>
    public const string Indeterminate = "indeterminate";

    /// <summary>The attempt committed its checkpoint.</summary>
    public const string Committed = "committed";

    /// <summary>The attempt committed, and releasing its resources then failed.</summary>
    public const string CommittedCleanupFailed = "committed_cleanup_failed";
}
