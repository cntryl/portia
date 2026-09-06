using System.Text.Json.Nodes;

namespace Cntryl.Portia;

/// <summary>
/// Walks a stored event's name/version forward through registered <see cref="IJsonDomainEventUpcaster" />
/// instances, one schema version at a time, until <see cref="DomainEventTypeCatalog" /> resolves a
/// CLR type — kept separate from <see cref="JsonDomainEventSerializer" /> so that class only
/// handles JSON envelope (de)serialization, not schema-evolution policy.
/// </summary>
/// <param name="catalog">Maps a logical name and schema version to its current CLR type.</param>
/// <param name="upcasters">Bridges a stored schema version with no exact catalog registration
/// forward to one that has.</param>
sealed class DomainEventSchemaResolver(
    DomainEventTypeCatalog catalog,
    IReadOnlyDictionary<(string Name, int FromVersion), IJsonDomainEventUpcaster> upcasters)
{
    readonly DomainEventTypeCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    readonly IReadOnlyDictionary<(string Name, int FromVersion), IJsonDomainEventUpcaster> _upcasters =
        upcasters ?? throw new ArgumentNullException(nameof(upcasters));

    /// <summary>
    /// Resolves the CLR type for a stored event's name/version, upcasting <paramref name="payload" />
    /// forward as needed until the catalog has an exact registration.
    /// </summary>
    /// <param name="name">The event's logical name.</param>
    /// <param name="version">The schema version the event was stored at.</param>
    /// <param name="payload">The stored payload, at <paramref name="version" />.</param>
    /// <returns>The resolved CLR type, and the payload upcast forward to match it.</returns>
    public (Type Type, JsonObject Payload) Resolve(string name, int version, JsonObject payload)
    {
        while (!_catalog.TryResolve(name, version, out _))
        {
            if (!_upcasters.TryGetValue((name, version), out var upcaster))
            {
                throw new InvalidOperationException(
                    $"No registered type or upcaster can bring event '{name}' schema version {version} forward.");
            }

            payload = upcaster.Upcast(payload);
            version++;
        }

        _ = _catalog.TryResolve(name, version, out var resolvedType);
        return (resolvedType!, payload);
    }
}
