using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Cntryl.Portia;

/// <summary>Verifies HTTP member metadata caching preserves every JSON contract boundary.</summary>
public sealed class HttpBindingCacheTests
{
    /// <summary>Options identities retain independent resolved wire names.</summary>
    [Fact]
    public void ShouldNotShareBindingMetadataAcrossApplicationOptions()
    {
        var alpha = Options("alpha");
        var beta = Options("beta");
        using var alphaBody = JsonDocument.Parse("{\"alpha\":1}");
        using var betaBody = JsonDocument.Parse("{\"beta\":2}");

        var first = Read<PlainBinding>(alphaBody.RootElement, alpha);
        var second = Read<PlainBinding>(betaBody.RootElement, beta);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
    }

    /// <summary>A cached property converter remains scoped to its own request member.</summary>
    [Fact]
    public void ShouldNotLeakCachedPropertyConverterToAnotherRequest()
    {
        var options = Options();
        using var converted = JsonDocument.Parse("{\"value\":\"0x10\"}");
        using var ordinary = JsonDocument.Parse("{\"value\":16}");

        Assert.Equal(16, Read<ConvertedBinding>(converted.RootElement, options));
        Assert.Equal(16, Read<PlainBinding>(ordinary.RootElement, options));
    }

    /// <summary>Concurrent first use resolves one immutable binding and returns correct values.</summary>
    [Fact]
    public async Task ShouldResolveBindingMetadataOnceDuringConcurrentFirstUse()
    {
        var resolutions = 0;
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(PlainBinding))
                _ = Interlocked.Increment(ref resolutions);
        });
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = resolver };
        using var body = JsonDocument.Parse("{\"value\":42}");

        var values = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => Read<PlainBinding>(body.RootElement, options))));

        Assert.All(values, value => Assert.Equal(42, value));
        Assert.Equal(1, resolutions);
    }

    /// <summary>Property-level number handling overrides otherwise strict options.</summary>
    [Fact]
    public void ShouldHonorCachedPropertyNumberHandling()
    {
        var options = Options();
        options.NumberHandling = JsonNumberHandling.Strict;
        using var body = JsonDocument.Parse("{\"value\":\"42\"}");

        Assert.Equal(42, Read<NumberBinding>(body.RootElement, options));
    }

    static int Read<TRequest>(JsonElement body, JsonSerializerOptions options) =>
        PortiaHttpBinding.ReadBody<TRequest, int>(body, options, 0, "Value", "value", false, false, 0);

    static JsonSerializerOptions Options(string? name = null)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        if (name is not null)
        {
            resolver.Modifiers.Add(info =>
            {
                if (info.Type == typeof(PlainBinding))
                    info.Properties[0].Name = name;
            });
        }

        return new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = resolver };
    }

    sealed record PlainBinding(int Value);

    sealed record ConvertedBinding([property: JsonConverter(typeof(HexConverter))] int Value);

    sealed record NumberBinding(
        [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] int Value);

    sealed class HexConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            int.Parse(reader.GetString()!.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value);
    }
}
