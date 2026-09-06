using System.Text.Json.Nodes;

namespace Cntryl.Portia;

/// <summary>
/// Transforms one schema version of a logical event's stored JSON payload into the next.
/// Explicitly belongs to the JSON serialization adapter: alternative
/// <see cref="IDomainEventSerializer" /> implementations can define evolution contracts for their
/// own payload representation without depending on <see cref="JsonObject" />.
/// </summary>
public interface IJsonDomainEventUpcaster
{
    /// <summary>
    /// Gets the logical event name this upcaster applies to.
    /// </summary>
    string EventName { get; }

    /// <summary>
    /// Gets the schema version this upcaster reads from; it produces
    /// <see cref="FromVersion" /> + 1.
    /// </summary>
    int FromVersion { get; }

    /// <summary>
    /// Transforms a JSON payload into the next schema version.
    /// </summary>
    /// <param name="payload">The stored payload at <see cref="FromVersion" />.</param>
    /// <returns>The payload reshaped for <see cref="FromVersion" /> + 1.</returns>
    JsonObject Upcast(JsonObject payload);
}
