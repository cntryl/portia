namespace Cntryl.Portia;

/// <summary>
///     Maps a logical event name and schema version (see <see cref="DiscriminatorAttribute" />) to the
///     concrete CLR type that currently represents it. Register every <see cref="DomainEvent" />
///     type a serializer needs to construct — including old, superseded versions you still want to
///     deserialize directly (rather than upcast through), if you've kept their CLR types around.
/// </summary>
public sealed class DomainEventTypeCatalog
{
    readonly Dictionary<Type, (string Name, int Version)> _discriminators = [];
    readonly Dictionary<(string Name, int Version), Type> _types = [];

    /// <summary>Registers the domain events declared by the calling compilation.</summary>
    /// <returns>This catalog, for chaining.</returns>
    /// <remarks>Portia's generator supplies the registration at each concrete call site.</remarks>
#pragma warning disable CA1822 // An instance entry point takes precedence over friend-visible legacy generated extensions.
    public DomainEventTypeCatalog AddPortiaGeneratedDomainEvents() =>
        throw new InvalidOperationException(
            "Portia.Generators did not intercept this event catalog registration. Ensure analyzer assets are enabled "
            + "and call AddPortiaGeneratedDomainEvents directly rather than through a method group or delegate.");
#pragma warning restore CA1822

    /// <summary>
    ///     Registers <typeparamref name="TEvent" /> under its own logical name and schema version.
    /// </summary>
    /// <typeparam name="TEvent">The event type to register.</typeparam>
    /// <param name="version">The schema version, starting at 1.</param>
    /// <param name="name">The logical event name.</param>
    /// <returns>This catalog, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The name and version pair, or the CLR type, is
    ///     already registered.
    /// </exception>
    public DomainEventTypeCatalog Register<TEvent>(int version, string name)
        where TEvent : DomainEvent
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        if (_types.TryGetValue((name, version), out var registered))
            throw new InvalidOperationException(
                $"Event '{name}' schema version {version} is already registered to type '{registered}'.");
        if (_discriminators.ContainsKey(typeof(TEvent)))
            throw new InvalidOperationException($"Event type '{typeof(TEvent)}' is already registered.");

        _types.Add((name, version), typeof(TEvent));
        _discriminators.Add(typeof(TEvent), (name, version));
        return this;
    }

    /// <summary>
    ///     Attempts to resolve the CLR type registered for an exact logical name and schema version.
    /// </summary>
    /// <param name="name">The logical event name.</param>
    /// <param name="version">The schema version.</param>
    /// <param name="type">The registered type, if found.</param>
    /// <returns><see langword="true" /> if a type is registered for that exact name and version.</returns>
    public bool TryResolve(string name, int version, out Type? type) =>
        _types.TryGetValue((name, version), out type);

    /// <summary>Gets the generated discriminator for a concrete event type.</summary>
    /// <param name="eventType">The concrete event type to look up.</param>
    /// <returns>The logical name and schema version registered for that type.</returns>
    /// <exception cref="InvalidOperationException">The type is not present in the catalog.</exception>
    public (string Name, int Version) Describe(Type eventType) =>
        _discriminators.TryGetValue(eventType, out var discriminator)
            ? discriminator
            : throw new InvalidOperationException(
                $"Event type '{eventType}' is not present in the generated contract catalog.");
}
