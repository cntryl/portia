using System.Text.Json;

namespace Cntryl.Portia;

/// <summary>Separates the exact broker campaign's serializer CPU and allocation from network waits.</summary>
[MemoryDiagnoser]
public class FitzSerializationCpuBenchmarks
{
    JsonDomainEventSerializer _serializer = null!;
    DomainEvent _event = null!;
    ReadOnlyMemory<byte> _encoded;

    /// <summary>Gets or sets the exact source-generated business JSON byte length.</summary>
    [Params(256, 4096)]
    public int PayloadBytes { get; set; }

    /// <summary>Prepares the same serializer, schema and metadata shape used by real Fitz appends.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _serializer = FitzAppendCampaign.Serializer();
        var overhead = JsonSerializer.SerializeToUtf8Bytes(new FitzCampaignEvent(string.Empty),
            FitzCampaignJsonContext.Default.FitzCampaignEvent).Length;
        _event = FitzAppendCampaign.CreateEvents(Uuid.CreateVersion4(), 0, 1, new string('a', PayloadBytes - overhead))[0];
        _encoded = _serializer.Serialize(_event);
        var decoded = (FitzCampaignEvent)_serializer.Deserialize(_encoded);
        if (decoded.Metadata != _event.Metadata || JsonSerializer.SerializeToUtf8Bytes(decoded,
                FitzCampaignJsonContext.Default.FitzCampaignEvent).Length != PayloadBytes)
            throw new InvalidOperationException("Matched Fitz serializer CPU fixture did not round-trip.");
    }

    /// <summary>Measures just complete durable envelope serialization, with no broker call.</summary>
    [Benchmark]
    public ReadOnlyMemory<byte> Serialize() => _serializer.Serialize(_event);

    /// <summary>Measures just the matching committed envelope's exact-version decoding.</summary>
    [Benchmark]
    public DomainEvent Deserialize() => _serializer.Deserialize(_encoded);
}
