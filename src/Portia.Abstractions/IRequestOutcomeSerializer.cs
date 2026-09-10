namespace Cntryl.Portia;

/// <summary>
///     Serializes request outcomes returned by an out-of-process handler.
/// </summary>
public interface IRequestOutcomeSerializer
{
    /// <summary>
    ///     Serializes the outcome of handling a no-result request.
    /// </summary>
    /// <param name="outcome">The outcome to serialize.</param>
    /// <returns>The wire representation.</returns>
    ReadOnlyMemory<byte> SerializeOutcome(Result outcome);

    /// <summary>
    ///     Serializes the outcome of handling a request that produces a result.
    /// </summary>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    /// <param name="result">The outcome to serialize.</param>
    /// <returns>The wire representation.</returns>
    ReadOnlyMemory<byte> SerializeResult<TOut>(Result<TOut> result);
}
