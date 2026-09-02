namespace Cntryl.Portia;

/// <summary>
/// Converts concrete requests, and the outcomes of handling them, to and from their durable
/// wire representation for out-of-process transports (Fitz RPC, Fitz queues). The actor token
/// carried alongside a request here is the end-user's bearer token — application-level data this
/// serializer just moves as opaque payload — never Fitz's own transport-level connection
/// credentials, which are Fitz's concern and entirely unrelated.
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

    /// <summary>
    /// Deserializes a concrete request, with or without a result, alongside the raw actor token
    /// that traveled with it.
    /// </summary>
    /// <param name="data">The wire representation.</param>
    /// <returns>The deserialized request and the raw actor token that traveled with it.</returns>
    (IRequestBase Request, string? ActorToken) DeserializeRequest(ReadOnlyMemory<byte> data);

    /// <summary>
    /// Serializes the outcome of handling a no-result request.
    /// </summary>
    /// <param name="outcome">The outcome to serialize.</param>
    /// <returns>The wire representation.</returns>
    ReadOnlyMemory<byte> SerializeOutcome(Result outcome);

    /// <summary>
    /// Deserializes the outcome of handling a no-result request.
    /// </summary>
    /// <param name="data">The wire representation.</param>
    /// <returns>The deserialized outcome.</returns>
    Result DeserializeOutcome(ReadOnlyMemory<byte> data);

    /// <summary>
    /// Serializes the outcome of handling a request that produces a result.
    /// </summary>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    /// <param name="result">The outcome to serialize.</param>
    /// <returns>The wire representation.</returns>
    ReadOnlyMemory<byte> SerializeResult<TOut>(Result<TOut> result);

    /// <summary>
    /// Deserializes the outcome of handling a request that produces a result.
    /// </summary>
    /// <typeparam name="TOut">The type of the value produced on success.</typeparam>
    /// <param name="data">The wire representation.</param>
    /// <returns>The deserialized outcome.</returns>
    Result<TOut> DeserializeResult<TOut>(ReadOnlyMemory<byte> data);
}
