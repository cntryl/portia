namespace Cntryl.Portia;

/// <summary>
/// Serializes concrete requests for an out-of-process transport.
/// </summary>
public interface IRequestSerializer
{
    /// <summary>
    /// Serializes a concrete request, with or without a result, alongside the raw actor token
    /// that should travel with it.
    /// </summary>
    /// <param name="request">The request to serialize.</param>
    /// <param name="actorToken">The actor's raw bearer token, or <see langword="null" /> for an
    /// unauthenticated actor. Re-validated (not just deserialized) at the point the request is
    /// actually dispatched — see <see cref="IRequestActorValidator" />.</param>
    /// <returns>The wire representation.</returns>
    ReadOnlyMemory<byte> Serialize(IRequestBase request, string? actorToken);
}

/// <summary>
/// Deserializes concrete requests received from an out-of-process transport.
/// </summary>
public interface IRequestDeserializer
{
    /// <summary>
    /// Deserializes a concrete request and its opaque actor token.
    /// </summary>
    /// <param name="data">The wire representation.</param>
    /// <returns>The deserialized request and actor token.</returns>
    (IRequestBase Request, string? ActorToken) DeserializeRequest(ReadOnlyMemory<byte> data);
}

/// <summary>
/// Serializes request outcomes returned by an out-of-process handler.
/// </summary>
public interface IRequestOutcomeSerializer
{
    /// <summary>
    /// Serializes the outcome of handling a no-result request.
    /// </summary>
    /// <param name="outcome">The outcome to serialize.</param>
    /// <returns>The wire representation.</returns>
    ReadOnlyMemory<byte> SerializeOutcome(Result outcome);

    /// <summary>
    /// Serializes the outcome of handling a request that produces a result.
    /// </summary>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    /// <param name="result">The outcome to serialize.</param>
    /// <returns>The wire representation.</returns>
    ReadOnlyMemory<byte> SerializeResult<TOut>(Result<TOut> result);
}

/// <summary>
/// Deserializes request outcomes received by an out-of-process caller.
/// </summary>
public interface IRequestOutcomeDeserializer
{
    /// <summary>
    /// Deserializes the outcome of handling a no-result request.
    /// </summary>
    /// <param name="data">The wire representation.</param>
    /// <returns>The deserialized outcome.</returns>
    Result DeserializeOutcome(ReadOnlyMemory<byte> data);

    /// <summary>
    /// Deserializes the outcome of handling a request that produces a result.
    /// </summary>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    /// <param name="data">The wire representation.</param>
    /// <returns>The deserialized outcome.</returns>
    Result<TOut> DeserializeResult<TOut>(ReadOnlyMemory<byte> data);
}
