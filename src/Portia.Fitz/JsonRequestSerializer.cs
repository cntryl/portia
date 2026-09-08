using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Cntryl.Portia;

/// <summary>Serializes Portia request and outcome envelopes without runtime type loading.</summary>
public sealed class JsonRequestSerializer : IRequestSerializer, IRequestDeserializer, IRequestOutcomeSerializer, IRequestOutcomeDeserializer
{
    readonly JsonSerializerOptions _options;
    readonly Dictionary<Type, Contract> _byType = [];
    readonly Dictionary<string, Contract> _byName = new(StringComparer.Ordinal);

    internal RequestTransportCatalog Catalog { get; }

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

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Serialize(IRequestBase request, string? actorToken, RequestMetadata metadata, RequestTraceContext? traceContext)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(metadata);
        metadata.Validate();
        var type = request.GetType();
        var descriptor = _byType.TryGetValue(type, out var registered)
            ? registered
            : throw new InvalidOperationException($"Request type '{type}' is not present in the generated contract catalog.");
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 2);
            writer.WriteString("contract", descriptor.Name);
            writer.WriteNumber("contract_version", descriptor.Version);
            writer.WritePropertyName("payload");
            JsonSerializer.Serialize(writer, request, descriptor.JsonTypeInfo);
            if (actorToken is not null) writer.WriteString("actor_token", actorToken);
            writer.WritePropertyName("metadata");
            JsonSerializer.Serialize(writer, metadata, FitzJsonContext.Default.RequestMetadata);
            if (traceContext?.TraceParent is not null) writer.WriteString("traceparent", traceContext.TraceParent);
            if (traceContext?.TraceState is not null) writer.WriteString("tracestate", traceContext.TraceState);
            writer.WriteEndObject();
        });
    }

    /// <inheritdoc />
    public DeserializedRequest DeserializeEnvelope(ReadOnlyMemory<byte> data)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        if (!root.TryGetProperty("version", out var version) || version.GetInt32() != 2)
            throw new InvalidOperationException("Unsupported request envelope version; Portia accepts version 2 only.");
        var name = RequiredString(root, "contract");
        var contractVersion = root.GetProperty("contract_version").GetInt32();
        if (!_byName.TryGetValue(Key(name, contractVersion), out var descriptor))
            throw new InvalidOperationException($"Unknown request contract '{name}' version {contractVersion}.");
        var metadata = JsonSerializer.Deserialize(root.GetProperty("metadata"), FitzJsonContext.Default.RequestMetadata)
            ?? throw new InvalidOperationException("A request envelope requires logical metadata.");
        metadata.Validate();
        var request = (IRequestBase?)JsonSerializer.Deserialize(root.GetProperty("payload"), descriptor.JsonTypeInfo)
            ?? throw new InvalidOperationException($"The '{name}' payload deserialized to null.");
        var actor = OptionalString(root, "actor_token");
        var traceparent = OptionalString(root, "traceparent");
        var trace = traceparent is null ? null : new RequestTraceContext(traceparent, OptionalString(root, "tracestate"));
        return new DeserializedRequest(request, actor, metadata, trace);
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> SerializeOutcome(Result outcome) => WriteOutcome(outcome.IsSuccess, null, outcome.Error);

    /// <inheritdoc />
    public Result DeserializeOutcome(ReadOnlyMemory<byte> data)
    {
        using var document = JsonDocument.Parse(data);
        var (success, error) = ReadOutcome(document.RootElement);
        return success ? Result.Success : Result.Failure(error!);
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> SerializeResult<TOut>(Result<TOut> result)
        => WriteOutcome(result.IsSuccess, result.IsSuccess ? JsonSerializer.SerializeToElement(
            result.Value, _options.GetTypeInfo(typeof(TOut))) : null, result.Error);

    /// <inheritdoc />
    public Result<TOut> DeserializeResult<TOut>(ReadOnlyMemory<byte> data)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var (success, error) = ReadOutcome(root);
        if (!success) return Result<TOut>.Failure(error!);
        if (!root.TryGetProperty("value", out var value))
            throw new InvalidOperationException("A successful result envelope requires a value.");
        var deserialized = JsonSerializer.Deserialize(value, (JsonTypeInfo<TOut>)_options.GetTypeInfo(typeof(TOut)));
        return Result<TOut>.Success(deserialized!);
    }

    void Add(Contract descriptor)
    {
        var key = Key(descriptor.Name, descriptor.Version);
        if (_byName.TryGetValue(key, out var existing) && existing.Type != descriptor.Type)
            throw new InvalidOperationException($"Duplicate request contract '{descriptor.Name}' version {descriptor.Version}.");
        _byType[descriptor.Type] = descriptor;
        _byName[key] = descriptor;
    }

    static ReadOnlyMemory<byte> WriteOutcome(bool success, JsonElement? value, RequestError? error) => Write(writer =>
    {
        if (success == (error is not null))
            throw new InvalidOperationException("An outcome must be either success or failure.");
        writer.WriteStartObject();
        writer.WriteBoolean("is_success", success);
        if (value is { } element) { writer.WritePropertyName("value"); element.WriteTo(writer); }
        if (error is not null) { writer.WritePropertyName("error"); JsonSerializer.Serialize(writer, error, FitzJsonContext.Default.RequestError); }
        writer.WriteEndObject();
    });

    static (bool Success, RequestError? Error) ReadOutcome(JsonElement root)
    {
        var success = root.GetProperty("is_success").GetBoolean();
        var error = root.TryGetProperty("error", out var property)
            ? JsonSerializer.Deserialize(property, FitzJsonContext.Default.RequestError) : null;
        return success != (error is not null)
            ? (success, error)
            : throw new InvalidOperationException("Malformed outcome envelope.");
    }

    static ReadOnlyMemory<byte> Write(Action<Utf8JsonWriter> action)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) action(writer);
        return stream.ToArray();
    }

    static string RequiredString(JsonElement root, string name)
        => OptionalString(root, name) ?? throw new InvalidOperationException($"Request envelope property '{name}' is required.");

    static string? OptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    static string Key(string name, int version) => string.Concat(name, "\0", version.ToString(System.Globalization.CultureInfo.InvariantCulture));

    sealed record Contract(string Name, int Version, Type Type, JsonTypeInfo JsonTypeInfo);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RequestMetadata))]
[JsonSerializable(typeof(RequestError))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(FitzScheduledRequestEnvelope))]
sealed partial class FitzJsonContext : JsonSerializerContext;
