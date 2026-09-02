using System.Reflection;

namespace Cntryl.Portia;

/// <summary>
/// Resolves a <see cref="DomainEvent" /> type's logical wire identity — see
/// <see cref="EventSchemaAttribute" /> for how it's declared and defaulted.
/// </summary>
public static class EventSchema
{
    /// <summary>
    /// Gets the logical event name and schema version for a <see cref="DomainEvent" /> type.
    /// </summary>
    /// <param name="eventType">The concrete event type.</param>
    /// <returns>The logical name and schema version.</returns>
    public static (string Name, int Version) For(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        var attribute = eventType.GetCustomAttribute<EventSchemaAttribute>();
        return (attribute?.Name ?? eventType.Name, attribute?.Version ?? 1);
    }
}
