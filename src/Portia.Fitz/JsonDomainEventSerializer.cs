using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cntryl.Portia;

/// <summary>
/// A reflection-at-the-wire-boundary (not hot-path) <see cref="IDomainEventSerializer" />: a JSON
/// envelope carrying an event's logical name and schema version (see
/// <see cref="EventSchemaAttribute" />), its metadata, and its business payload. Deserializing
/// resolves the event's current CLR type via <see cref="DomainEventTypeCatalog" /> — an exact
/// name-and-version match resolves directly, letting old, superseded CLR types stay registered
/// and readable forever with no upcasting at all; anything else is walked forward one schema
/// version at a time through registered <see cref="IDomainEventUpcaster" /> instances until it
/// lands on a version the catalog does have.
/// </summary>
public sealed class JsonDomainEventSerializer : IDomainEventSerializer
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    readonly DomainEventTypeCatalog _catalog;
    readonly Dictionary<(string Name, int FromVersion), IDomainEventUpcaster> _upcasters;

    /// <summary>
    /// Creates a JSON domain-event serializer.
    /// </summary>
    /// <param name="catalog">Maps a logical name and schema version to its current CLR type.</param>
    /// <param name="upcasters">
    /// Bridges a stored schema version with no exact catalog registration forward to one that
    /// has. Each event name/version pair may have at most one registered upcaster.
    /// </param>
    public JsonDomainEventSerializer(DomainEventTypeCatalog catalog, IEnumerable<IDomainEventUpcaster>? upcasters = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        _catalog = catalog;
        _upcasters = [];

        foreach (var upcaster in upcasters ?? [])
        {
            var key = (upcaster.EventName, upcaster.FromVersion);
            if (!_upcasters.TryAdd(key, upcaster))
            {
                throw new InvalidOperationException(
                    $"An upcaster from event '{upcaster.EventName}' schema version {upcaster.FromVersion} is already registered.");
            }
        }
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Serialize(DomainEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        var type = ev.GetType();
        var (name, version) = EventSchema.For(type);
        var envelope = new EventEnvelope(
            name,
            version,
            JsonSerializer.SerializeToNode(ev.Metadata, Options)!.AsObject(),
            JsonSerializer.SerializeToNode(ev, type, Options)!.AsObject());

        return JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
    }

    /// <inheritdoc />
    public DomainEvent Deserialize(ReadOnlyMemory<byte> data)
    {
        var envelope = JsonSerializer.Deserialize<EventEnvelope>(data.Span, Options)
            ?? throw new InvalidOperationException("The event envelope deserialized to null.");

        var name = envelope.Name;
        var version = envelope.Version;
        var payload = envelope.Payload;

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
        var ev = (DomainEvent?)payload.Deserialize(resolvedType!, Options)
            ?? throw new InvalidOperationException($"The '{resolvedType}' payload deserialized to null.");

        var metadata = envelope.Metadata.Deserialize<DomainEventMetadata>(Options)
            ?? throw new InvalidOperationException("The event metadata deserialized to null.");
        ev.AttachMetadata(metadata);

        return ev;
    }

    sealed record EventEnvelope(string Name, int Version, JsonObject Metadata, JsonObject Payload);
}
