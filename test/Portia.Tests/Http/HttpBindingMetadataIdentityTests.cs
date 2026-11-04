using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;

namespace Cntryl.Portia;

/// <summary>Verifies HTTP metadata binds members by their declared identity.</summary>
public sealed class HttpBindingMetadataIdentityTests
{
    /// <summary>Renamed JSON properties retain their identity when constructor order differs.</summary>
    [Fact]
    public void ShouldPreserveConstructorMemberIdentityGivenRenamedJsonProperties()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(ReorderedBinding))
                return;
            foreach (var property in info.Properties)
                property.Name = "wire_" + property.Name;
        });
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = resolver };
        using var body = JsonDocument.Parse("{\"wire_a\":11,\"wire_b\":22}");

        var first = PortiaHttpBinding.ReadBody<ReorderedBinding, int>(body.RootElement, options,
            0, "A", "a", false, false, 0);
        var second = PortiaHttpBinding.ReadBody<ReorderedBinding, int>(body.RootElement, options,
            1, "B", "b", false, false, 0);

        Assert.Equal(11, first);
        Assert.Equal(22, second);
    }

    /// <summary>Constructor association takes precedence over another member's original wire name.</summary>
    [Fact]
    public void ShouldUseMemberIdentityGivenSwappedWireNames()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(ReorderedBinding))
                foreach (var property in info.Properties)
                    property.Name = property.Name == "a" ? "b" : "a";
        });
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = resolver };
        using var body = JsonDocument.Parse("{\"a\":22,\"b\":11}");

        Assert.Equal(11, PortiaHttpBinding.ReadBody<ReorderedBinding, int>(body.RootElement, options,
            0, "a", "a", false, false, 0));
        Assert.Equal(22, PortiaHttpBinding.ReadBody<ReorderedBinding, int>(body.RootElement, options,
            1, "b", "b", false, false, 0));
    }

    /// <summary>JSON constructor order does not replace the public constructor's declared member identity.</summary>
    [Fact]
    public void ShouldUseDeclaredMemberIdentityGivenDifferentJsonConstructorOrder()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(AlternateConstructorBinding))
                foreach (var property in info.Properties)
                    property.Name = "wire_" + property.Name;
        });
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = resolver };
        using var body = JsonDocument.Parse("{\"wire_a\":11}");

        Assert.True(PortiaHttpBinding.HasBodyMember<AlternateConstructorBinding, long>(body.RootElement, options,
            0, "a", "a"));
        Assert.Equal(11L, PortiaHttpBinding.ReadBody<AlternateConstructorBinding, long>(body.RootElement, options,
            0, "a", "a", false, false, 0L));
        PortiaHttpBinding.ValidateRoutedBody<AlternateConstructorBinding, long>(body.RootElement, options,
            0, "a", "a", 11L);
    }

    /// <summary>Previously generated body lookups retain their declared member despite a reordered JSON constructor.</summary>
    [Fact]
    public void ShouldPreserveLegacyBodyLookupGivenDifferentJsonConstructorOrder()
    {
        var options = RenamedLegacyOptions<LegacyConstructorBinding>();
        using var body = JsonDocument.Parse("{\"wire_a\":11}");

        Assert.True(PortiaHttpBinding.HasBodyMember<LegacyConstructorBinding, int>(body.RootElement, options,
            0, "a"));
    }

    /// <summary>Previously generated route validation must reject a conflicting declared member.</summary>
    [Fact]
    public void ShouldRejectLegacyRoutedBodyConflictGivenDifferentJsonConstructorOrder()
    {
        var options = RenamedLegacyOptions<LegacyConstructorBinding>();
        using var body = JsonDocument.Parse("{\"wire_a\":999,\"wire_b\":11}");

        Assert.Throws<BadHttpRequestException>(() =>
            PortiaHttpBinding.ValidateRoutedBody<LegacyConstructorBinding, int>(body.RootElement, options,
                0, "a", 11));
    }

    /// <summary>Legacy helpers retain explicit aliases and member converters after metadata renaming.</summary>
    [Fact]
    public void ShouldPreserveLegacyAliasAndConverterGivenRenamedMetadata()
    {
        var options = RenamedLegacyOptions<AliasedLegacyBinding>();
        using var body = JsonDocument.Parse("{\"wire_alias_a\":\"0x10\"}");
        using var conflict = JsonDocument.Parse("{\"wire_alias_a\":\"0x20\"}");

        Assert.True(PortiaHttpBinding.HasBodyMember<AliasedLegacyBinding, int>(body.RootElement, options,
            0, "alias_a"));
        PortiaHttpBinding.ValidateRoutedBody<AliasedLegacyBinding, int>(body.RootElement, options,
            0, "alias_a", 16);
        Assert.Throws<BadHttpRequestException>(() =>
            PortiaHttpBinding.ValidateRoutedBody<AliasedLegacyBinding, int>(conflict.RootElement, options,
                0, "alias_a", 16));
    }

    /// <summary>Unidentifiable metadata is rejected rather than using an unrelated property's position.</summary>
    [Fact]
    public void ShouldRejectUnidentifiableConstructorMemberGivenReplacedMetadata()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(ReorderedBinding))
                return;
            info.Properties.Clear();
            info.Properties.Add(info.CreateJsonPropertyInfo(typeof(int), "unrelated"));
        });
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = resolver };
        using var body = JsonDocument.Parse("{\"unrelated\":22}");

        Assert.Throws<InvalidOperationException>(() => PortiaHttpBinding.ReadBody<ReorderedBinding, int>(
            body.RootElement, options, 0, "a", "a", false, false, 0));
    }

    /// <summary>Parameterless JSON construction still retains the declared member metadata.</summary>
    [Fact]
    public void ShouldUseDeclaredMemberIdentityGivenParameterlessJsonConstructor()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(ParameterlessJsonBinding))
                foreach (var property in info.Properties)
                    property.Name = "wire_" + property.Name;
        });
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = resolver };
        using var body = JsonDocument.Parse("{\"wire_a\":11,\"wire_b\":22}");

        Assert.Equal(11, PortiaHttpBinding.ReadBody<ParameterlessJsonBinding, int>(body.RootElement, options,
            0, "a", "a", false, false, 0));
        Assert.Equal(22, PortiaHttpBinding.ReadBody<ParameterlessJsonBinding, int>(body.RootElement, options,
            1, "b", "b", false, false, 0));
    }

    /// <summary>Generated metadata preserves member converters and constructor identity without reflection fallback.</summary>
    [Fact]
    public void ShouldPreserveMemberConverterGivenSourceGeneratedReorderedMetadata()
    {
        var options = HttpMemberMetadataJsonContext.Default.Options;
        using var body = JsonDocument.Parse("{\"first\":\"0x10\",\"second\":32}");

        Assert.Equal(16, PortiaHttpBinding.ReadBody<HttpMemberMetadataBinding, int>(body.RootElement, options,
            0, "First", "first", false, false, 0));
        Assert.Equal(32, PortiaHttpBinding.ReadBody<HttpMemberMetadataBinding, int>(body.RootElement, options,
            1, "Second", "second", false, false, 0));
    }

    static JsonSerializerOptions RenamedLegacyOptions<T>()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(T))
                foreach (var property in info.Properties)
                    property.Name = "wire_" + property.Name;
        });
        return new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = resolver };
    }

    sealed class ReorderedBinding(int a, int b)
    {
        public int B { get; } = b;
        public int A { get; } = a;
    }

    sealed class AlternateConstructorBinding(long a, int b)
    {
        public int B { get; } = b;
        public long A { get; } = a;

        [JsonConstructor]
        AlternateConstructorBinding(int b, long a) : this(a, b) { }
    }

    sealed class LegacyConstructorBinding
    {
        public LegacyConstructorBinding(int a, int b) : this(b, a, false) { }

        [JsonConstructor]
        LegacyConstructorBinding(int b, int a, bool marker)
        {
            A = a;
            B = b;
            Marker = marker;
        }

        public int A { get; }
        public int B { get; }
        public bool Marker { get; }
    }

    sealed record AliasedLegacyBinding(
        [property: JsonPropertyName("alias_a"), JsonPropertyOrder(2), JsonConverter(typeof(HttpMemberHexConverter))] int A,
        [property: JsonPropertyOrder(1)] int B);

    sealed class ParameterlessJsonBinding(int a, int b)
    {
        public int B { get; set; } = b;
        public int A { get; set; } = a;

        [JsonConstructor]
        ParameterlessJsonBinding() : this(0, 0) { }
    }
}

sealed record HttpMemberMetadataBinding(
    [property: JsonPropertyOrder(2), JsonConverter(typeof(HttpMemberHexConverter))] int First,
    [property: JsonPropertyOrder(1)] int Second);

sealed class HttpMemberHexConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        int.Parse(reader.GetString()!.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteStringValue("0x" + value.ToString("X", CultureInfo.InvariantCulture));
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HttpMemberMetadataBinding))]
sealed partial class HttpMemberMetadataJsonContext : JsonSerializerContext;
