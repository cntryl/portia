namespace Cntryl.Portia;

/// <summary>
/// Receives no-result requests delivered live, with no queueing or redelivery — a request
/// delivered while nothing is consuming is lost. Shared by any live-delivery transport (Fitz
/// notice fanout, a fired Fitz schedule entry, or anything else with the same delivery model).
/// </summary>
public interface ILiveRequestConsumer
{
    /// <summary>
    /// Reads requests as they are delivered.
    /// </summary>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>Requests in the order they are delivered.</returns>
    IAsyncEnumerable<LiveRequest> ReadAsync(CancellationToken ct = default);
}
