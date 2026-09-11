namespace Cntryl.Portia;

/// <summary>
///     Handles a queued request before Portia acknowledges a terminal delivery. Applications must
///     register this capability whenever permanent queue failures are possible; missing or failed
///     handling faults the worker and leaves ownership with the transport.
/// </summary>
public interface IQueuedRequestTerminalHandler
{
    /// <summary>
    ///     Handles the terminal failure. Returning successfully permits acknowledgment. Because
    ///     callback completion and transport acknowledgment are not atomic, implementations must
    ///     tolerate the same terminal context being delivered again.
    /// </summary>
    /// <param name="context">The failed request, its delivery, and the error that ended it.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task that completes once the failure has been handled.</returns>
    ValueTask HandleAsync(QueuedRequestFailureContext context, CancellationToken ct = default);
}
