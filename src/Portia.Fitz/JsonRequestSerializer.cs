using System.Text.Json;

namespace Cntryl.Portia;

/// <summary>
/// A reflection-at-the-wire-boundary (not hot-path) serializer for Fitz RPC and queue transports:
/// a small JSON envelope carrying the request's assembly-qualified type
/// name, its own JSON payload, and the actor token that traveled with it. Resolving the type name
/// back to a <see cref="Type" /> on deserialize is the one place this uses reflection — everywhere
/// else in the framework stays reflection-free by design, but a wire deserializer has no way
/// around needing to know what type it's even deserializing into.
/// </summary>
public sealed class JsonRequestSerializer :
    IRequestSerializer,
    IRequestDeserializer,
    IRequestOutcomeSerializer,
    IRequestOutcomeDeserializer
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Serialize(IRequestBase request, string? actorToken)
        => Serialize(request, actorToken, RequestMetadata.Create());

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Serialize(IRequestBase request, string? actorToken, RequestMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(metadata);
        metadata.Validate();

        var envelope = new RequestEnvelope(
            request.GetType().AssemblyQualifiedName ?? throw new InvalidOperationException($"Type '{request.GetType()}' has no assembly-qualified name."),
            JsonSerializer.SerializeToElement(request, request.GetType(), Options),
            actorToken, 1, metadata);

        return JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
    }

    /// <inheritdoc />
    public (IRequestBase Request, string? ActorToken) DeserializeRequest(ReadOnlyMemory<byte> data)
    {
        var envelope = DeserializeEnvelope(data);
        return (envelope.Request, envelope.ActorToken);
    }

    /// <inheritdoc />
    public DeserializedRequest DeserializeEnvelope(ReadOnlyMemory<byte> data)
    {
        var envelope = JsonSerializer.Deserialize<RequestEnvelope>(data.Span, Options)
            ?? throw new InvalidOperationException("The request envelope deserialized to null.");

        if (envelope.Version != 1)
            throw new InvalidOperationException("Unsupported request envelope version.");
        if (envelope.Metadata is null)
            throw new InvalidOperationException("A versioned request envelope requires logical metadata.");
        envelope.Metadata.Validate();
        var type = Type.GetType(envelope.Type, throwOnError: true)!;
        var request = (IRequestBase?)envelope.Payload.Deserialize(type, Options)
            ?? throw new InvalidOperationException($"The '{type}' payload deserialized to null.");

        return new DeserializedRequest(request, envelope.ActorToken, envelope.Metadata);
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> SerializeOutcome(Result outcome) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new OutcomeEnvelope(outcome.IsSuccess, ValueElement: null, outcome.Error),
            Options);

    /// <inheritdoc />
    public Result DeserializeOutcome(ReadOnlyMemory<byte> data)
    {
        var envelope = JsonSerializer.Deserialize<OutcomeEnvelope>(data.Span, Options)
            ?? throw new InvalidOperationException("The outcome envelope deserialized to null.");

        return envelope.IsSuccess ? Result.Success : Result.Failure(envelope.Error!);
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> SerializeResult<TOut>(Result<TOut> result) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new OutcomeEnvelope(
                result.IsSuccess,
                result.IsSuccess ? JsonSerializer.SerializeToElement(result.Value, Options) : null,
                result.Error),
            Options);

    /// <inheritdoc />
    public Result<TOut> DeserializeResult<TOut>(ReadOnlyMemory<byte> data)
    {
        var envelope = JsonSerializer.Deserialize<OutcomeEnvelope>(data.Span, Options)
            ?? throw new InvalidOperationException("The outcome envelope deserialized to null.");

        return envelope.IsSuccess
            ? Result<TOut>.Success(envelope.ValueElement!.Value.Deserialize<TOut>(Options)!)
            : Result<TOut>.Failure(envelope.Error!);
    }

    sealed record RequestEnvelope(string Type, JsonElement Payload, string? ActorToken, int? Version = null, RequestMetadata? Metadata = null);

    sealed record OutcomeEnvelope(bool IsSuccess, JsonElement? ValueElement, RequestError? Error);
}
