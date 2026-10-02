using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;

namespace Cntryl.Portia;

/// <summary>Measures complete durable envelopes, including causal and actor metadata.</summary>
[MemoryDiagnoser]
public class EnvelopeLifecycleBenchmarks
{
    EnvelopeEvent _event = null!;
    JsonDomainEventSerializer _serializer = null!;
    ReadOnlyMemory<byte> _current;
    ReadOnlyMemory<byte> _legacy;

    /// <summary>Gets or sets the business payload character count, before JSON escaping.</summary>
    [Params(256, 4096, 65536)]
    public int PayloadLength { get; set; }

    /// <summary>Gets or sets ASCII or escaping-heavy text; both include a custom numeric converter.</summary>
    [Params("ascii", "escaped")]
    public string Text { get; set; } = "ascii";

    /// <summary>Freezes metadata, encoded input and a real one-version upcast outside measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _serializer = new JsonDomainEventSerializer(
            new DomainEventTypeCatalog().Register<EnvelopeEvent>(2, "benchmark.envelope"),
            [new EnvelopeUpcaster()], EnvelopeJsonContext.Default.Options);
        var unit = Text == "ascii" ? "a" : "é\"\\\n";
        var text = string.Concat(Enumerable.Repeat(unit, (PayloadLength + unit.Length - 1) / unit.Length))[..PayloadLength];
        _event = new EnvelopeEvent(text, 123456);
        _event.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), Uuid.CreateVersion4(), 42,
            DateTimeOffset.UnixEpoch, Uuid.CreateVersion4(), Uuid.CreateVersion4(), IsAudit: true)
        {
            ExecutionId = Uuid.CreateVersion4(),
            Actor = new ActorAttribution("benchmark-actor", "benchmark-authority")
        });
        _current = _serializer.Serialize(_event);
        var legacy = JsonNode.Parse(_current.Span)!.AsObject();
        legacy["version"] = 1;
        var payload = legacy["payload"]!.AsObject();
        var value = payload["Payload"];
        payload.Remove("Payload");
        payload["LegacyPayload"] = value;
        _legacy = JsonSerializer.SerializeToUtf8Bytes(legacy, EnvelopeJsonContext.Default.JsonObject);
        Qualify(_current);
        Qualify(_legacy);
        if (!_current.Span.SequenceEqual(_serializer.Serialize(_event).Span))
            throw new InvalidOperationException("Frozen envelope serialization was not deterministic.");
    }

    void Qualify(ReadOnlyMemory<byte> data)
    {
        var result = (EnvelopeEvent)_serializer.Deserialize(data);
        if (result.Payload != _event.Payload || result.Number != _event.Number || result.Metadata != _event.Metadata)
            throw new InvalidOperationException("Envelope payload, converter or full metadata did not round-trip.");
    }

    /// <summary>Serializes payload and full metadata into a durable envelope.</summary>
    [Benchmark]
    public ReadOnlyMemory<byte> Serialize() => _serializer.Serialize(_event);

    /// <summary>Reads the exact registered schema without constructing an upcast DOM.</summary>
    [Benchmark]
    public DomainEvent DeserializeExact() => _serializer.Deserialize(_current);

    /// <summary>Reads a legacy schema through one actual bounded upcaster.</summary>
    [Benchmark]
    public DomainEvent DeserializeUpcast() => _serializer.Deserialize(_legacy);
}

[Discriminator("benchmark.envelope", 2)]
sealed record EnvelopeEvent(string Payload, [property: JsonConverter(typeof(EnvelopeHexConverter))] int Number) : DomainEvent;

sealed class EnvelopeUpcaster : IJsonDomainEventUpcaster
{
    public string EventName => "benchmark.envelope";
    public int FromVersion => 1;
    public JsonObject Upcast(JsonObject payload)
    {
        var value = payload["LegacyPayload"];
        payload.Remove("LegacyPayload");
        payload["Payload"] = value;
        return payload;
    }
}

sealed class EnvelopeHexConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        int.Parse(reader.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("X", CultureInfo.InvariantCulture));
}

[PortiaJsonContext]
[JsonSerializable(typeof(EnvelopeEvent))]
[JsonSerializable(typeof(JsonObject))]
sealed partial class EnvelopeJsonContext : JsonSerializerContext;
