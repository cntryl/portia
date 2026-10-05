namespace Cntryl.Portia;

/// <summary>
///     Legacy exception type from the former required-terminal-handler contract. Built-in queue
///     runners no longer throw it because terminal observers are optional.
/// </summary>
/// <param name="reason">Why the delivery became terminal.</param>
public sealed class TerminalHandlerMissingException(QueuedRequestTerminalReason reason)
    : Exception($"A queued request became terminal ({reason}), but no IQueuedRequestTerminalHandler is registered.")
{
    /// <summary>Gets why the delivery became terminal.</summary>
    public QueuedRequestTerminalReason Reason { get; } = reason;
}
