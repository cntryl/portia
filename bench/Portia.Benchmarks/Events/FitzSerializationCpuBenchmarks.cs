using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

/// <summary>Measures durable event envelope serializer CPU and allocation without broker network waits.</summary>
[MemoryDiagnoser]
public class FitzSerializationCpuBenchmarks
{
    JsonDomainEventSerializer _serializer = null!;
    DomainEvent _event = null!;
    ReadOnlyMemory<byte> _encoded;

    /// <summary>Gets or sets the exact source-generated business JSON byte length.</summary>
    [Params(256, 4096)]
    public int PayloadBytes { get; set; }

    /// <summary>Prepares the serializer, schema and metadata shape used by real Fitz appends.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _serializer = new JsonDomainEventSerializer(
            new DomainEventTypeCatalog().Register<FitzSerializationEvent>(1, "benchmark.fitz.append"), null,
            FitzSerializationJsonContext.Default.Options);
        var overhead = JsonSerializer.SerializeToUtf8Bytes(new FitzSerializationEvent(string.Empty),
            FitzSerializationJsonContext.Default.FitzSerializationEvent).Length;
        var ev = new FitzSerializationEvent(new string('a', PayloadBytes - overhead));
        ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), Uuid.CreateVersion4(), 1, DateTimeOffset.UnixEpoch));
        _event = ev;
        _encoded = _serializer.Serialize(_event);
        var decoded = (FitzSerializationEvent)_serializer.Deserialize(_encoded);
        if (decoded.Metadata != _event.Metadata || JsonSerializer.SerializeToUtf8Bytes(decoded,
                FitzSerializationJsonContext.Default.FitzSerializationEvent).Length != PayloadBytes)
            throw new InvalidOperationException("Fitz serializer CPU fixture did not round-trip.");
    }

    /// <summary>Measures just complete durable envelope serialization, with no broker call.</summary>
    [Benchmark]
    public ReadOnlyMemory<byte> Serialize() => _serializer.Serialize(_event);

    /// <summary>Measures just the matching committed envelope's exact-version decoding.</summary>
    [Benchmark]
    public DomainEvent Deserialize() => _serializer.Deserialize(_encoded);
}

[Discriminator("benchmark.fitz.append")]
sealed record FitzSerializationEvent(string Payload) : DomainEvent;

[PortiaJsonContext]
[JsonSerializable(typeof(FitzSerializationEvent))]
sealed partial class FitzSerializationJsonContext : JsonSerializerContext;
