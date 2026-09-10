namespace Cntryl.Portia;

/// <summary>Controls application-owned queue terminal disposition.</summary>
public sealed class QueueRunnerOptions
{
    /// <summary>Gets or sets the transport-reported attempt at which retryable failures become terminal.</summary>
    public uint? TerminalAttempt { get; set; }
}
