namespace Cntryl.Portia;

/// <summary>Handles a queued request before Portia acknowledges a terminal delivery.</summary>
public interface IQueuedRequestTerminalHandler
{
    /// <summary>Handles the terminal failure. Returning successfully permits acknowledgment.</summary>
    /// <param name="context">The failed request, its delivery, and the error that ended it.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task that completes once the failure has been handled.</returns>
    ValueTask HandleAsync(QueuedRequestFailureContext context, CancellationToken ct = default);
}
