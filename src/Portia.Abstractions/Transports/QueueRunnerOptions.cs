namespace Cntryl.Portia;

/// <summary>Controls queue retry delay and application-owned terminal disposition.</summary>
public sealed class QueueRunnerOptions
{
    /// <summary>Gets or sets the transport-reported attempt at which retryable failures become terminal.</summary>
    public uint? TerminalAttempt { get; set; }

    /// <summary>Gets or sets the delay before failed deliveries become visible again, or null for transport defaults.</summary>
    /// <remarks>Must be nonnegative whole seconds. Zero explicitly requests immediate retry.</remarks>
    public TimeSpan? RetryDelay { get; set; }

    internal void Validate()
    {
        if (TerminalAttempt == 0)
            throw new QueueConfigurationException("QueueRunnerOptions.TerminalAttempt must be positive when configured.");
        if (RetryDelay is { } delay && (delay < TimeSpan.Zero || delay.Ticks % TimeSpan.TicksPerSecond != 0))
            throw new QueueConfigurationException("QueueRunnerOptions.RetryDelay must be nonnegative whole seconds when configured.");
    }
}
