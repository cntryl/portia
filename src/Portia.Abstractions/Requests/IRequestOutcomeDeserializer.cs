namespace Cntryl.Portia;

/// <summary>
///     Deserializes request outcomes received by an out-of-process caller.
/// </summary>
public interface IRequestOutcomeDeserializer
{
    /// <summary>
    ///     Deserializes the outcome of handling a no-result request.
    /// </summary>
    /// <param name="data">The wire representation.</param>
    /// <returns>The deserialized outcome.</returns>
    Result DeserializeOutcome(ReadOnlyMemory<byte> data);

    /// <summary>
    ///     Deserializes the outcome of handling a request that produces a result.
    /// </summary>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    /// <param name="data">The wire representation.</param>
    /// <returns>The deserialized outcome.</returns>
    Result<TOut> DeserializeResult<TOut>(ReadOnlyMemory<byte> data);
}
