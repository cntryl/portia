using System.Text.Json.Nodes;

namespace Cntryl.Portia;

/// <summary>
/// Transforms one schema version of a logical event's stored JSON payload into the next. A
/// serializer chains upcasters (see <see cref="DomainEventTypeCatalog" />) starting from the
/// version an event was actually stored at, one step at a time, until it reaches a version with
/// a registered CLR type — so a type can be renamed or reshaped without ever rewriting history.
/// </summary>
public interface IDomainEventUpcaster
{
    /// <summary>
    /// Gets the logical event name this upcaster applies to (see <see cref="EventSchemaAttribute" />).
    /// </summary>
    string EventName { get; }

    /// <summary>
    /// Gets the schema version this upcaster reads from — it produces <see cref="FromVersion" /> + 1.
    /// </summary>
    int FromVersion { get; }

    /// <summary>
    /// Transforms a payload stored at <see cref="FromVersion" /> into the shape expected at
    /// <see cref="FromVersion" /> + 1.
    /// </summary>
    /// <param name="payload">The stored payload, at <see cref="FromVersion" />.</param>
    /// <returns>The payload reshaped for <see cref="FromVersion" /> + 1.</returns>
    JsonObject Upcast(JsonObject payload);
}
