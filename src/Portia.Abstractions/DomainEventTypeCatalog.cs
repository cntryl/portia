namespace Cntryl.Portia;

/// <summary>
/// Maps a logical event name and schema version (see <see cref="EventSchemaAttribute" />) to the
/// concrete CLR type that currently represents it. Register every <see cref="DomainEvent" />
/// type a serializer needs to construct — including old, superseded versions you still want to
/// deserialize directly (rather than upcast through), if you've kept their CLR types around.
/// </summary>
public sealed class DomainEventTypeCatalog
{
    readonly Dictionary<(string Name, int Version), Type> _types = [];

    /// <summary>
    /// Registers <typeparamref name="TEvent" /> under its own logical name and schema version.
    /// </summary>
    /// <typeparam name="TEvent">The event type to register.</typeparam>
    /// <returns>This catalog, for chaining.</returns>
    public DomainEventTypeCatalog Register<TEvent>()
        where TEvent : DomainEvent
    {
        var (name, version) = EventSchema.For(typeof(TEvent));

        return _types.TryAdd((name, version), typeof(TEvent))
            ? this
            : throw new InvalidOperationException(
                $"Event '{name}' schema version {version} is already registered to type '{_types[(name, version)]}'.");
    }

    /// <summary>
    /// Attempts to resolve the CLR type registered for an exact logical name and schema version.
    /// </summary>
    /// <param name="name">The logical event name.</param>
    /// <param name="version">The schema version.</param>
    /// <param name="type">The registered type, if found.</param>
    /// <returns><see langword="true" /> if a type is registered for that exact name and version.</returns>
    public bool TryResolve(string name, int version, out Type? type) =>
        _types.TryGetValue((name, version), out type);
}
