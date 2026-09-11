using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cntryl.Portia;

/// <summary>Serializes Portia request and outcome envelopes without runtime type loading.</summary>
public sealed class JsonRequestSerializer : IRequestSerializer, IRequestDeserializer, IRequestOutcomeSerializer,
    IRequestOutcomeDeserializer
{
    const int DefaultEnvelopeBytes = 512;

    readonly Dictionary<(string Name, int Version), Contract> _byName = [];
    readonly Dictionary<Type, Contract> _byType = [];
    readonly JsonSerializerOptions _options;

    /// <summary>Creates a serializer from a generated request catalog and frozen JSON options.</summary>
    public JsonRequestSerializer(IEnumerable<RequestTransportRegistration> registrations, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        var descriptors = registrations.ToArray();
        Catalog = new RequestTransportCatalog(descriptors);
        foreach (var registration in descriptors)
        {
            var descriptor = new Contract(registration.Discriminator.Name, registration.Discriminator.Version,
                registration.RequestType, _options.GetTypeInfo(registration.RequestType));
            Add(descriptor);
        }
    }

    internal RequestTransportCatalog Catalog { get; }

    /// <inheritdoc />
    public DeserializedRequest DeserializeEnvelope(ReadOnlyMemory<byte> data)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        if (!root.TryGetProperty("version", out var version) || version.GetInt32() != 2)
        {
            throw new InvalidOperationException("Unsupported request envelope version; Portia accepts version 2 only.");
        }

        var name = RequiredString(root, "contract");
        var contractVersion = root.GetProperty("contract_version").GetInt32();
        if (!_byName.TryGetValue((name, contractVersion), out var descriptor))
        {
            throw new InvalidOperationException($"Unknown request contract '{name}' version {contractVersion}.");
        }

        var metadata = root.GetProperty("metadata").Deserialize(FitzJsonContext.Default.RequestMetadata)
                       ?? throw new InvalidOperationException("A request envelope requires logical metadata.");
        metadata.Validate();
        var request = (IRequestBase?)root.GetProperty("payload").Deserialize(descriptor.JsonTypeInfo)
                      ?? throw new InvalidOperationException($"The '{name}' payload deserialized to null.");
        var actor = OptionalString(root, "actor_token");
        var traceparent = OptionalString(root, "traceparent");
        var trace = traceparent is null
            ? null
            : new RequestTraceContext(traceparent, OptionalString(root, "tracestate"));
        return new DeserializedRequest(request, name, actor, metadata, trace);
    }

    /// <inheritdoc />
    public Result DeserializeOutcome(ReadOnlyMemory<byte> data)
    {
        using var document = JsonDocument.Parse(data);
        var (success, error) = ReadOutcome(document.RootElement);
        return success ? Result.Success : Result.Failure(error!);
    }

    /// <inheritdoc />
    public Result<TOut> DeserializeResult<TOut>(ReadOnlyMemory<byte> data)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var (success, error) = ReadOutcome(root);
        if (!success)
        {
            return Result<TOut>.Failure(error!);
        }

        if (!root.TryGetProperty("value", out var value))
        {
            throw new InvalidOperationException("A successful result envelope requires a value.");
        }

        var deserialized = value.Deserialize((JsonTypeInfo<TOut>)_options.GetTypeInfo(typeof(TOut)));
        return Result<TOut>.Success(deserialized!);
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> SerializeOutcome(Result outcome) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteBoolean("is_success", outcome.IsSuccess);
        if (!outcome.IsSuccess)
        {
            writer.WritePropertyName("error");
            JsonSerializer.Serialize(writer, outcome.Error, FitzJsonContext.Default.RequestError);
        }

        writer.WriteEndObject();
    });

    /// <inheritdoc />
    public ReadOnlyMemory<byte> SerializeResult<TOut>(Result<TOut> result)
    {
        // The value is written straight into the envelope. Serializing it to a JsonElement first
        // built a second, complete copy of the payload only to copy it out again.
        var typeInfo = (JsonTypeInfo<TOut>)_options.GetTypeInfo(typeof(TOut));
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("is_success", result.IsSuccess);
            if (result.IsSuccess)
            {
                writer.WritePropertyName("value");
                JsonSerializer.Serialize(writer, result.Value, typeInfo);
            }
            else
            {
                writer.WritePropertyName("error");
                JsonSerializer.Serialize(writer, result.Error, FitzJsonContext.Default.RequestError);
            }

            writer.WriteEndObject();
        });
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Serialize(IRequestBase request, string? actorToken, RequestMetadata metadata,
        RequestTraceContext? traceContext)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(metadata);
        metadata.Validate();
        var type = request.GetType();
        var descriptor = _byType.TryGetValue(type, out var registered)
            ? registered
            : throw new InvalidOperationException(
                $"Request type '{type}' is not present in the generated contract catalog.");
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 2);
            writer.WriteString("contract", descriptor.Name);
            writer.WriteNumber("contract_version", descriptor.Version);
            writer.WritePropertyName("payload");
            JsonSerializer.Serialize(writer, request, descriptor.JsonTypeInfo);
            if (actorToken is not null)
            {
                writer.WriteString("actor_token", actorToken);
            }

            writer.WritePropertyName("metadata");
            JsonSerializer.Serialize(writer, metadata, FitzJsonContext.Default.RequestMetadata);
            if (traceContext?.TraceParent is not null)
            {
                writer.WriteString("traceparent", traceContext.TraceParent);
            }

            if (traceContext?.TraceState is not null)
            {
                writer.WriteString("tracestate", traceContext.TraceState);
            }

            writer.WriteEndObject();
        });
    }

    void Add(Contract descriptor)
    {
        var key = (descriptor.Name, descriptor.Version);
        if (_byName.TryGetValue(key, out var existing) && existing.Type != descriptor.Type)
        {
            throw new InvalidOperationException(
                $"Duplicate request contract '{descriptor.Name}' version {descriptor.Version}.");
        }

        // One type serializes under one discriminator: letting a second registration silently
        // replace the first would make the same request leave on two different contracts.
        if (_byType.TryGetValue(descriptor.Type, out var registered) &&
            (registered.Name != descriptor.Name || registered.Version != descriptor.Version))
        {
            throw new InvalidOperationException(
                $"Request type '{descriptor.Type}' is registered under more than one discriminator.");
        }

        _byType[descriptor.Type] = descriptor;
        _byName[key] = descriptor;
    }

    static (bool Success, RequestError? Error) ReadOutcome(JsonElement root)
    {
        var success = root.GetProperty("is_success").GetBoolean();
        var error = root.TryGetProperty("error", out var property)
            ? property.Deserialize(FitzJsonContext.Default.RequestError)
            : null;
        return success != (error is not null)
            ? (success, error)
            : throw new InvalidOperationException("Malformed outcome envelope.");
    }

    // Writing through a buffer writer hands back the bytes that were written. A MemoryStream plus
    // ToArray built the envelope once and then copied the whole thing again for every message.
    static ReadOnlyMemory<byte> Write(Action<Utf8JsonWriter> action)
    {
        var buffer = new ArrayBufferWriter<byte>(DefaultEnvelopeBytes);
        using (var writer = new Utf8JsonWriter(buffer))
            action(writer);
        return buffer.WrittenMemory;
    }

    static string RequiredString(JsonElement root, string name)
        => OptionalString(root, name) ??
           throw new InvalidOperationException($"Request envelope property '{name}' is required.");

    static string? OptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    sealed record Contract(string Name, int Version, Type Type, JsonTypeInfo JsonTypeInfo);
}
