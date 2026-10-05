namespace Cntryl.Portia;

/// <summary>Owns the application dependencies and optional terminal observer for one queue delivery.</summary>
public interface IQueueDeliveryScope : IRequestDeliveryScope
{
    /// <summary>Gets the terminal-attempt policy.</summary>
    QueueRunnerOptions Options { get; }

    /// <summary>Gets the optional application terminal-failure observer.</summary>
    IQueuedRequestTerminalHandler? TerminalHandler { get; }
}
