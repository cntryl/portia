namespace Cntryl.Portia;

/// <summary>
/// Declares the durable wire identity of a <see cref="DomainEvent" /> type — a logical event
/// name and schema version, independent of the CLR type name. Without this attribute, the
/// logical name defaults to the CLR type's simple name and the version defaults to 1.
/// </summary>
/// <remarks>
/// Renaming a CLR type is safe without this attribute only as long as the old and new names
/// never need to coexist in the same wire history. Once a type has ever been persisted, giving
/// it an explicit <paramref name="name" /> decouples its stored identity from its C# name, so a
/// later rename (or a schema-breaking replacement type) doesn't strand every previously
/// persisted event.
/// </remarks>
/// <param name="name">
/// The logical event name. Two types with different <see cref="Version" /> values may share the
/// same <paramref name="name" /> — see <see cref="DomainEventTypeCatalog" />.
/// </param>
/// <param name="version">The schema version this type represents, starting at 1.</param>
[AttributeUsage(AttributeTargets.Class)]
public sealed class EventSchemaAttribute(string? name = null, int version = 1) : Attribute
{
    /// <summary>
    /// Gets the logical event name, or <see langword="null" /> to default to the CLR type's
    /// simple name.
    /// </summary>
    public string? Name { get; } = name;

    /// <summary>
    /// Gets the schema version this type represents.
    /// </summary>
    public int Version { get; } = version;
}
