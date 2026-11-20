using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

/// <summary>Reads and writes exact decimal strings in JSON.</summary>
public sealed class DecimalStringJsonConverter : JsonConverter<DecimalString>
{
    /// <inheritdoc />
    public override DecimalString Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && DecimalString.TryParse(reader.GetString(), out var value)
            ? value
            : throw new JsonException("Expected an exact decimal string.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DecimalString value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
