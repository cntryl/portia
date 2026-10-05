namespace Cntryl.Portia;

/// <summary>
///     Observes a terminal queued-request failure before Portia returns the unacknowledged delivery
///     to its transport. Register this capability when the application needs terminal-failure
///     reporting; it is not required for queue hosting and cannot acknowledge the delivery.
/// </summary>
public interface IQueuedRequestTerminalHandler
{
    /// <summary>
    ///     Observes the terminal failure. A successful return does not acknowledge the delivery;
    ///     its transport owns redelivery and dead-letter policy. Since the transport may redeliver
    ///     the same failure, implementations must tolerate replay.
    /// </summary>
    /// <param name="context">The failed request, its delivery, and the error that ended it.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task that completes once the failure has been handled.</returns>
    ValueTask HandleAsync(QueuedRequestFailureContext context, CancellationToken ct = default);
}
