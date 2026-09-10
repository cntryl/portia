namespace Cntryl.Portia;

/// <summary>Handles a queued request before Portia acknowledges a terminal delivery.</summary>
public interface IQueuedRequestTerminalHandler
{
    /// <summary>Handles the terminal failure. Returning successfully permits acknowledgment.</summary>
    ValueTask HandleAsync(QueuedRequestFailureContext context, CancellationToken ct = default);
}
