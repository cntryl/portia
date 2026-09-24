namespace Cntryl.Portia;

/// <summary>Controls application-owned queue terminal disposition.</summary>
public sealed class QueueRunnerOptions
{
    /// <summary>Gets or sets the transport-reported attempt at which retryable failures become terminal.</summary>
    public uint? TerminalAttempt { get; set; }

    internal void Validate()
    {
        if (TerminalAttempt == 0)
            throw new QueueConfigurationException("QueueRunnerOptions.TerminalAttempt must be positive when configured.");
    }
}
