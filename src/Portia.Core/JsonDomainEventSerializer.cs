using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cntryl.Portia;

/// <summary>
///     A reflection-at-the-wire-boundary (not hot-path) <see cref="IDomainEventSerializer" />: a JSON
///     envelope carrying an event's logical name and schema version (see
///     <see cref="DiscriminatorAttribute" />), its metadata, and its business payload. Deserializing
///     resolves the event's current CLR type via <see cref="DomainEventTypeCatalog" /> — an exact
///     name-and-version match resolves directly, letting old, superseded CLR types stay registered
///     and readable forever with no upcasting at all; anything else is walked forward one schema
///     version at a time through registered <see cref="IJsonDomainEventUpcaster" /> instances until it
///     lands on a version the catalog does have.
/// </summary>
public sealed class JsonDomainEventSerializer : IDomainEventSerializer
{
    readonly DomainEventTypeCatalog _catalog;
    readonly JsonSerializerOptions _options;
    readonly DomainEventSchemaResolver _resolver;

    /// <summary>Creates a serializer using the application's frozen source-generated JSON options.</summary>
    /// <param name="catalog">Maps each logical event name and schema version to its current CLR type.</param>
    /// <param name="upcasters">
    ///     The registered payload upcasters, or <see langword="null" /> when the
    ///     application registers none.
    /// </param>
    /// <param name="options">The application's frozen Portia JSON options.</param>
    /// <exception cref="InvalidOperationException">
    ///     An upcaster is malformed or duplicated, or a chain
    ///     of upcasters cannot reach a version the catalog knows.
    /// </exception>
    public JsonDomainEventSerializer(DomainEventTypeCatalog catalog, IEnumerable<IJsonDomainEventUpcaster>? upcasters,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _catalog = catalog;

        var upcastersByKey = new Dictionary<(string Name, int FromVersion), IJsonDomainEventUpcaster>();
        var errors = new List<(string Name, int Version, string Message)>();

        foreach (var upcaster in upcasters ?? [])
        {
            if (upcaster.EventName is not { } eventName || string.IsNullOrWhiteSpace(eventName))
            {
                errors.Add((upcaster.EventName ?? string.Empty, upcaster.FromVersion,
                    "An upcaster has an empty event name."));
                continue;
            }

            var key = (eventName, upcaster.FromVersion);
            if (upcaster.FromVersion is <= 0 or int.MaxValue)
            {
                errors.Add((upcaster.EventName, upcaster.FromVersion,
                    $"Event '{upcaster.EventName}' has invalid upcaster source version {upcaster.FromVersion}."));
            }
            else
            {
                if (!upcastersByKey.TryAdd(key, upcaster))
                {
                    errors.Add((upcaster.EventName, upcaster.FromVersion,
                        $"Event '{upcaster.EventName}' has duplicate upcasters from schema version {upcaster.FromVersion}."));
                }
            }
        }

        foreach (var ((name, fromVersion), _) in upcastersByKey)
        {
            // The walk only ever moves forward one schema version at a time, so it terminates at
            // the first version with neither a registered type nor an upcaster to bridge it.
            var version = fromVersion + 1;
            while (!catalog.TryResolve(name, version, out _))
            {
                if (!upcastersByKey.ContainsKey((name, version)))
                {
                    errors.Add((name, version,
                        $"Event '{name}' is missing an upcaster from schema version {version}."));
                    break;
                }

                version++;
            }
        }

        _resolver = errors.Count > 0
            ? throw new InvalidOperationException("Invalid JSON domain-event upcaster registrations:" +
                                                Environment.NewLine
                                                + string.Join(Environment.NewLine, errors.Distinct()
                                                    .OrderBy(error => error.Name, StringComparer.Ordinal)
                                                    .ThenBy(error => error.Version)
                                                    .Select(error => $"- {error.Message}")))
            : new DomainEventSchemaResolver(catalog, upcastersByKey);
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Serialize(DomainEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        var type = ev.GetType();
        var (name, version) = _catalog.Describe(type);
        var metadata = JsonSerializer.SerializeToUtf8Bytes(ev.Metadata,
            PortiaCoreJsonContext.Default.DomainEventMetadata);
        var payload = JsonSerializer.SerializeToUtf8Bytes(ev, _options.GetTypeInfo(type));
        var capacity = checked(metadata.Length + payload.Length + name.Length * 6 + 64);
        var buffer = new ArrayBufferWriter<byte>(capacity);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteNumber("version", version);
            writer.WritePropertyName("metadata");
            writer.WriteRawValue(metadata, skipInputValidation: true);
            writer.WritePropertyName("payload");
            writer.WriteRawValue(payload, skipInputValidation: true);
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory;
    }

    /// <inheritdoc />
    public DomainEvent Deserialize(ReadOnlyMemory<byte> data)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var name = root.GetProperty("name").GetString()
                   ?? throw new InvalidOperationException("The event envelope requires a name.");
        var version = root.GetProperty("version").GetInt32();
        var payloadElement = root.GetProperty("payload");
        DomainEvent? ev;
        if (_catalog.TryResolve(name, version, out var resolvedType) && resolvedType is not null)
        {
            ev = (DomainEvent?)payloadElement.Deserialize(_options.GetTypeInfo(resolvedType));
        }
        else
        {
            var payload = payloadElement.Deserialize(PortiaCoreJsonContext.Default.JsonObject)
                          ?? throw new InvalidOperationException("The event payload deserialized to null.");
            (resolvedType, payload) = _resolver.Resolve(name, version, payload);
            ev = (DomainEvent?)payload.Deserialize(_options.GetTypeInfo(resolvedType));
        }

        if (ev is null)
            throw new InvalidOperationException($"The '{resolvedType}' payload deserialized to null.");
        var metadata = root.GetProperty("metadata")
                           .Deserialize(PortiaCoreJsonContext.Default.DomainEventMetadata)
                       ?? throw new InvalidOperationException("The event metadata deserialized to null.");
        ev.AttachMetadata(metadata);

        return ev;
    }
}
