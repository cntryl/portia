using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

/// <summary>
///     Serializes a <see cref="TenantId" /> as its bare realm segment, as a JSON string or a property name.
///     Applied through <c>[JsonConverter]</c> on the type itself, so no serializer configuration is needed.
/// </summary>
public sealed class TenantIdJsonConverter : JsonConverter<TenantId>
{
    /// <inheritdoc />
    public override TenantId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TenantId.TryParse(reader.GetString(), null, out var tenant)
            ? tenant
            : throw new JsonException($"'{reader.GetString()}' is not a valid tenant ID.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);

    /// <inheritdoc />
    public override TenantId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => Read(ref reader, typeToConvert, options);

    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Value);
}
