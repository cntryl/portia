namespace Cntryl.Portia;

/// <summary>
/// Reserves requests off a queue for dispatch through the request bus, independent of which
/// queue technology (Fitz, a message broker, or anything else) backs it.
/// </summary>
public interface IRequestQueueConsumer
{
    /// <summary>
    /// Reserves queued requests as they become available.
    /// </summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>Reserved requests in the order they should be dispatched.</returns>
    IAsyncEnumerable<IQueuedRequest> ReadAsync(CancellationToken ct = default);
}
