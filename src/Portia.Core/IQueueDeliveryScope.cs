namespace Cntryl.Portia;

/// <summary>Owns the application dependencies and terminal policy used for one queue delivery.</summary>
public interface IQueueDeliveryScope : IRequestDeliveryScope
{
    /// <summary>Gets the terminal-attempt policy.</summary>
    QueueRunnerOptions Options { get; }

    /// <summary>Gets the optional terminal-failure handler.</summary>
    IQueuedRequestTerminalHandler? TerminalHandler { get; }
}
