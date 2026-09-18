using BenchmarkDotNet.Attributes;
using Cntryl.Portia.Testing;

namespace Cntryl.Portia;

/// <summary>Measures source-generated domain-event envelope serialization.</summary>
[MemoryDiagnoser]
public class DomainEventSerializationBenchmarks
{
    ReadOnlyMemory<byte> _data;
    SerializationBenchmarkEvent _event = null!;
    JsonDomainEventSerializer _serializer = null!;

    /// <summary>Gets or sets the event payload length.</summary>
    [Params(16, 256, 4096)]
    public int PayloadLength { get; set; }

    /// <summary>Creates a stable serializer, event, and encoded envelope.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var catalog = new DomainEventTypeCatalog().Register<SerializationBenchmarkEvent>(1,
            "benchmark.serialization");
        _serializer = new JsonDomainEventSerializer(catalog, null, BenchmarkJsonContext.Default.Options);
        _event = DomainEventSeed.Attach(new SerializationBenchmarkEvent(new string('a', PayloadLength)),
            Uuid.CreateVersion4(), 1, occurredOn: DateTimeOffset.UnixEpoch);
        _data = _serializer.Serialize(_event);
    }

    /// <summary>Serializes a fully identified event into its durable envelope.</summary>
    [Benchmark]
    public ReadOnlyMemory<byte> Serialize() => _serializer.Serialize(_event);

    /// <summary>Deserializes an exact-version durable envelope.</summary>
    [Benchmark]
    public DomainEvent Deserialize() => _serializer.Deserialize(_data);
}

[Discriminator("benchmark.serialization")]
sealed record SerializationBenchmarkEvent(string Payload) : DomainEvent;
