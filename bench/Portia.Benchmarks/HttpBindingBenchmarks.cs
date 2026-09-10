using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using BenchmarkDotNet.Attributes;

namespace Cntryl.Portia;

/// <summary>Measures generated HTTP member binding after body parsing.</summary>
[MemoryDiagnoser]
public class HttpBindingBenchmarks : IDisposable
{
    JsonDocument _defaultBody = null!;
    JsonSerializerOptions _defaultOptions = null!;
    JsonDocument _hexBody = null!;
    JsonSerializerOptions _hexOptions = null!;
    JsonDocument _mixedBody = null!;
    JsonSerializerOptions _mixedOptions = null!;
    string[] _mixedClrNames = null!;
    string[] _mixedJsonNames = null!;
    JsonDocument _numberBody = null!;
    JsonSerializerOptions _numberOptions = null!;

    /// <summary>Pre-parses bodies so warm measurements isolate member binding.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _defaultBody = JsonDocument.Parse("{\"value\":42}");
        _hexBody = JsonDocument.Parse("{\"value\":\"0x2a\"}");
        _numberBody = JsonDocument.Parse("{\"value\":\"42\"}");
        _mixedBody = JsonDocument.Parse("{" + string.Join(',', Enumerable.Range(1, 20)
            .Select(index => $"\"p{index}\":{index}")) + "}");
        _defaultOptions = Options();
        _hexOptions = Options();
        _numberOptions = Options();
        _mixedOptions = Options();
        _mixedClrNames = Enumerable.Range(1, 20).Select(index => $"P{index}").ToArray();
        _mixedJsonNames = Enumerable.Range(1, 20).Select(index => $"p{index}").ToArray();

        _ = DefaultMetadata();
        _ = PropertyConverter();
        _ = PropertyNumberHandling();
        _ = MixedTwentyMembers();
    }

    /// <summary>Reads a member using unmodified application options.</summary>
    [Benchmark(Baseline = true)]
    public int DefaultMetadata() => PortiaHttpBinding.ReadBody<DefaultBinding, int>(
        _defaultBody.RootElement, _defaultOptions, 0, "Value", "value", false, false, 0);

    /// <summary>Reads a member using a cached property converter override.</summary>
    [Benchmark]
    public int PropertyConverter() => PortiaHttpBinding.ReadBody<ConverterBinding, int>(
        _hexBody.RootElement, _hexOptions, 0, "Value", "value", false, false, 0);

    /// <summary>Reads a member using cached property number handling.</summary>
    [Benchmark]
    public int PropertyNumberHandling() => PortiaHttpBinding.ReadBody<NumberBinding, int>(
        _numberBody.RootElement, _numberOptions, 0, "Value", "value", false, false, 0);

    /// <summary>Reads a realistic mixed request's complete 20-member body.</summary>
    [Benchmark]
    public int MixedTwentyMembers()
    {
        var total = 0;
        for (var index = 0; index < 20; index++)
        {
            total += PortiaHttpBinding.ReadBody<MixedBinding, int>(_mixedBody.RootElement, _mixedOptions, index,
                _mixedClrNames[index], _mixedJsonNames[index], false, false, 0);
        }

        return total;
    }

    /// <summary>Measures uncached property-converter initialization with a fresh options identity.</summary>
    [Benchmark]
    public int ColdPropertyConverter()
    {
        var options = Options();
        return PortiaHttpBinding.ReadBody<ConverterBinding, int>(
            _hexBody.RootElement, options, 0, "Value", "value", false, false, 0);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _defaultBody.Dispose();
        _hexBody.Dispose();
        _numberBody.Dispose();
        _mixedBody.Dispose();
        GC.SuppressFinalize(this);
    }

    static JsonSerializerOptions Options() => new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    sealed record DefaultBinding(int Value);

    sealed record ConverterBinding([property: JsonConverter(typeof(HexIntConverter))] int Value);

    sealed record NumberBinding(
        [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] int Value);

    sealed record MixedBinding(
        int P1, int P2, int P3, int P4, int P5,
        int P6, int P7, int P8, int P9, int P10,
        int P11, int P12, int P13, int P14, int P15,
        int P16, int P17, int P18, int P19, int P20);

    sealed class HexIntConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            int.Parse(reader.GetString()!.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
            writer.WriteStringValue($"0x{value:x}");
    }
}
