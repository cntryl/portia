using System.Text.Json;

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
            var version = fromVersion + 1;
            var visited = new HashSet<int>();
            while (!catalog.TryResolve(name, version, out _))
            {
                if (!visited.Add(version) || !upcastersByKey.ContainsKey((name, version)))
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
