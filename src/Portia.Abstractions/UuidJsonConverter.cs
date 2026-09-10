using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

/// <summary>
///     Serializes a <see cref="Uuid" /> as its canonical string form, not as its default struct
///     shape (which would otherwise just be the public <see cref="Uuid.Version" /> property).
/// </summary>
public sealed class UuidJsonConverter : JsonConverter<Uuid>
{
    /// <inheritdoc />
    public override Uuid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Uuid.TryParse(reader.GetString(), out var uuid)
            ? uuid
            : throw new JsonException($"'{reader.GetString()}' is not a valid UUID.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Uuid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
