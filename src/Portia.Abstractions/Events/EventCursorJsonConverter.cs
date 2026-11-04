using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

/// <summary>Preserves opaque progress cursors as JSON strings, using an empty string for the start position.</summary>
/// <remarks>Apply to a cursor property explicitly; this converter does not change the global event-cursor contract.</remarks>
public sealed class EventCursorJsonConverter : JsonConverter<EventCursor>
{
    /// <inheritdoc />
    public override EventCursor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("An event cursor must be a JSON string.");
        var value = reader.GetString()!;
        if (value.Length == 0)
            return EventCursor.Start;
        if (string.IsNullOrWhiteSpace(value))
            throw new JsonException("An event cursor must not contain only whitespace.");
        return new EventCursor(value);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, EventCursor value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value ?? string.Empty);
    }
}
