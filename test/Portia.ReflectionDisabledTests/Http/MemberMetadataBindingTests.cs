using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Portia.ReflectionDisabled;

public sealed class MemberMetadataBindingTests
{
    [Fact]
    public void ShouldPreserveMemberIdentityAndConverterGivenReflectionDisabledMetadata()
    {
        // Arrange
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var options = MemberMetadataJsonContext.Default.Options;
        using var body = JsonDocument.Parse("{\"first\":\"0x10\",\"second\":32}");

        // Act
        var first = PortiaHttpBinding.ReadBody<MemberMetadataBinding, int>(body.RootElement, options,
            0, "First", "first", false, false, 0);
        var second = PortiaHttpBinding.ReadBody<MemberMetadataBinding, int>(body.RootElement, options,
            1, "Second", "second", false, false, 0);

        // Assert
        Assert.Equal(16, first);
        Assert.Equal(32, second);
    }

    [Fact]
    public void ShouldPreserveInitializerIdentityGivenReflectionDisabledStructMetadata()
    {
        // Arrange
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var options = MemberMetadataJsonContext.Default.Options;
        using var body = JsonDocument.Parse("{\"wire_first\":11,\"wire_second\":22}");

        // Act
        var first = PortiaHttpBinding.ReadBody<MemberMetadataStruct, int>(body.RootElement, options,
            0, "First", "first", false, false, 0);
        var second = PortiaHttpBinding.ReadBody<MemberMetadataStruct, int>(body.RootElement, options,
            1, "Second", "second", false, false, 0);

        // Assert
        Assert.Equal(11, first);
        Assert.Equal(22, second);
    }
}

sealed record MemberMetadataBinding(
    [property: JsonPropertyOrder(2), JsonConverter(typeof(MemberMetadataHexConverter))] int First,
    [property: JsonPropertyOrder(1)] int Second);

readonly record struct MemberMetadataStruct(
    [property: JsonPropertyName("wire_first"), JsonPropertyOrder(2)] int First,
    [property: JsonPropertyName("wire_second"), JsonPropertyOrder(1)] int Second);

sealed class MemberMetadataHexConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        int.Parse(reader.GetString()!.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteStringValue("0x" + value.ToString("X", CultureInfo.InvariantCulture));
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(MemberMetadataBinding))]
[JsonSerializable(typeof(MemberMetadataStruct))]
sealed partial class MemberMetadataJsonContext : JsonSerializerContext;
