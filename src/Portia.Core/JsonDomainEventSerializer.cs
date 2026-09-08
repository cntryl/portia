using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

/// <summary>
/// A reflection-at-the-wire-boundary (not hot-path) <see cref="IDomainEventSerializer" />: a JSON
/// envelope carrying an event's logical name and schema version (see
/// <see cref="DiscriminatorAttribute" />), its metadata, and its business payload. Deserializing
/// resolves the event's current CLR type via <see cref="DomainEventTypeCatalog" /> — an exact
/// name-and-version match resolves directly, letting old, superseded CLR types stay registered
/// and readable forever with no upcasting at all; anything else is walked forward one schema
/// version at a time through registered <see cref="IJsonDomainEventUpcaster" /> instances until it
/// lands on a version the catalog does have.
/// </summary>
public sealed class JsonDomainEventSerializer : IDomainEventSerializer
{
    readonly DomainEventSchemaResolver _resolver;
    readonly DomainEventTypeCatalog _catalog;
    readonly JsonSerializerOptions _options;

    /// <summary>Creates a serializer using the application's frozen source-generated JSON options.</summary>
    public JsonDomainEventSerializer(DomainEventTypeCatalog catalog, IEnumerable<IJsonDomainEventUpcaster>? upcasters, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _catalog = catalog;

        var upcastersByKey = new Dictionary<(string Name, int FromVersion), IJsonDomainEventUpcaster>();

        foreach (var upcaster in upcasters ?? [])
        {
            var key = (upcaster.EventName, upcaster.FromVersion);
            if (!upcastersByKey.TryAdd(key, upcaster))
            {
                throw new InvalidOperationException(
                    $"An upcaster from event '{upcaster.EventName}' schema version {upcaster.FromVersion} is already registered.");
            }
        }

        _resolver = new DomainEventSchemaResolver(catalog, upcastersByKey);
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Serialize(DomainEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        var type = ev.GetType();
        var (name, version) = _catalog.Describe(type);
        var envelope = new EventEnvelope(
            name,
            version,
            JsonSerializer.SerializeToNode(ev.Metadata, PortiaCoreJsonContext.Default.DomainEventMetadata)!.AsObject(),
            JsonSerializer.SerializeToNode(ev, _options.GetTypeInfo(type))!.AsObject());

        return JsonSerializer.SerializeToUtf8Bytes(envelope, PortiaCoreJsonContext.Default.EventEnvelope);
    }

    /// <inheritdoc />
    public DomainEvent Deserialize(ReadOnlyMemory<byte> data)
    {
        var envelope = JsonSerializer.Deserialize(data.Span, PortiaCoreJsonContext.Default.EventEnvelope)
            ?? throw new InvalidOperationException("The event envelope deserialized to null.");

        var (resolvedType, payload) = _resolver.Resolve(envelope.Name, envelope.Version, envelope.Payload);
        var ev = (DomainEvent?)payload.Deserialize(_options.GetTypeInfo(resolvedType))
            ?? throw new InvalidOperationException($"The '{resolvedType}' payload deserialized to null.");

        var metadata = envelope.Metadata.Deserialize(PortiaCoreJsonContext.Default.DomainEventMetadata)
            ?? throw new InvalidOperationException("The event metadata deserialized to null.");
        ev.AttachMetadata(metadata);

        return ev;
    }

}

sealed record EventEnvelope(string Name, int Version, JsonObject Metadata, JsonObject Payload);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(EventEnvelope))]
[JsonSerializable(typeof(DomainEventMetadata))]
sealed partial class PortiaCoreJsonContext : JsonSerializerContext;
