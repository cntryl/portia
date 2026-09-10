namespace Cntryl.Portia;

/// <summary>
///     Deserializes concrete requests received from an out-of-process transport.
/// </summary>
public interface IRequestDeserializer
{
    /// <summary>Reads the request, opaque actor token, logical metadata, and optional trace context.</summary>
    /// <param name="data">The transport wire representation.</param>
    /// <returns>The fully deserialized request envelope.</returns>
    DeserializedRequest DeserializeEnvelope(ReadOnlyMemory<byte> data);
}
