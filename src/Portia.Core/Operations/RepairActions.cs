namespace Cntryl.Portia;

/// <summary>The actions a <see cref="RepairPlan" /> can propose.</summary>
/// <remarks>These are the exact strings stored in the journal and returned to operators.</remarks>
public static class RepairActions
{
    /// <summary>Continue from the authoritative checkpoint when there is no failed source unit to replay.</summary>
    public const string Resume = "resume";

    /// <summary>Run the failed unit again.</summary>
    public const string Retry = "retry";
}
