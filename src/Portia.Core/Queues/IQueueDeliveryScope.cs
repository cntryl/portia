namespace Cntryl.Portia;

/// <summary>Owns the application dependencies and terminal policy used for one queue delivery.</summary>
public interface IQueueDeliveryScope : IRequestDeliveryScope
{
    /// <summary>Gets the terminal-attempt policy.</summary>
    QueueRunnerOptions Options { get; }

    /// <summary>Gets the application-selected terminal-failure handler required by queue runners.</summary>
    IQueuedRequestTerminalHandler? TerminalHandler { get; }
}
