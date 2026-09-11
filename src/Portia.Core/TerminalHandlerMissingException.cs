namespace Cntryl.Portia;

/// <summary>
///     Thrown when a queued delivery is terminal but the application has not registered its
///     terminal-failure handler. The delivery remains unacknowledged and transport-owned.
/// </summary>
/// <param name="reason">Why the delivery became terminal.</param>
public sealed class TerminalHandlerMissingException(QueuedRequestTerminalReason reason)
    : Exception($"A queued request became terminal ({reason}), but no IQueuedRequestTerminalHandler is registered.")
{
    /// <summary>Gets why the delivery became terminal.</summary>
    public QueuedRequestTerminalReason Reason { get; } = reason;
}
