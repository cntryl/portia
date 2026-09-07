namespace Cntryl.Portia;

/// <summary>
/// Receives one-way request notifications, with no queueing or redelivery — a notification sent
/// while nothing is consuming is lost. Shared by live notice fanout, fired schedule entries,
/// and anything else with the same delivery model.
/// </summary>
public interface IRequestNotificationConsumer
{
    /// <summary>
    /// Reads requests as they are delivered.
    /// </summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>Requests in the order they are delivered.</returns>
    IAsyncEnumerable<RequestNotification> ReadAsync(CancellationToken ct = default);
}
