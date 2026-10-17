using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Cntryl.Portia;

// Let the serializer identify schema locations, including dictionary values and polymorphic
// branches. Its callback omits some properties with null defaults; their containing object still
// supplies the exact property metadata needed to complete those locations.
sealed class PortiaOpenApiJsonSchemaExporter(JsonSerializerOptions options, Func<Type, Type?>? schemaType)
{
    readonly Dictionary<Type, string> _activeMappings = [];
    readonly HashSet<JsonNode> _processed = new(ReferenceEqualityComparer.Instance);

    public JsonNode Export(Type type) => Export(type, []);

    JsonNode Export(Type type, string[] prefix) =>
        options.GetTypeInfo(type).GetJsonSchemaAsNode(new JsonSchemaExporterOptions
        {
            TransformSchemaNode = (context, schema) =>
            {
                var path = prefix.Concat(context.Path.ToArray()).ToArray();
                var contractType = context.Path.IsEmpty ? type
                    : context.BaseTypeInfo is not null ? context.TypeInfo.Type
                    : context.PropertyInfo?.PropertyType ?? context.TypeInfo.Type;
                var normalized = Normalize(schema, contractType);
                if (normalized is JsonObject reference && reference["$ref"] is JsonValue value &&
                    value.TryGetValue<string>(out var target) && target.StartsWith('#'))
                    reference["$ref"] = Pointer(prefix) + target[1..];

                var mapped = Map(normalized, contractType, path, context.PropertyInfo);
                var result = mapped ?? normalized;
                if (mapped is null)
                    CompleteChildren(result, context.TypeInfo, path);
                _ = _processed.Add(result);
                return result;
            }
        });

    void CompleteChildren(JsonNode schema, JsonTypeInfo typeInfo, string[] path)
    {
        if (schema is not JsonObject obj || obj.ContainsKey("$ref"))
            return;
        var keyword = typeInfo.Kind switch
        {
            JsonTypeInfoKind.Dictionary => "additionalProperties",
            JsonTypeInfoKind.Enumerable => "items",
            _ => null
        };
        if (keyword is not null && typeInfo.ElementType is { } elementType)
        {
            var original = obj[keyword];
            var child = original ?? new JsonObject();
            if (!_processed.Contains(child))
            {
                var normalized = Normalize(child, elementType);
                var mapped = Map(normalized, elementType, [.. path, keyword], null);
                if (original is not null || mapped is not null || elementType == typeof(Uuid) || elementType == typeof(Uuid?))
                {
                    obj[keyword] = mapped ?? normalized;
                    _ = _processed.Add(obj[keyword]!);
                }
            }
            return;
        }
        if (typeInfo.Kind != JsonTypeInfoKind.Object || obj["properties"] is not JsonObject properties)
            return;

        foreach (var property in typeInfo.Properties)
        {
            if (properties[property.Name] is not { } original || _processed.Contains(original))
                continue;
            var normalized = Normalize(original, property.PropertyType);
            var result = Map(normalized, property.PropertyType, [.. path, "properties", property.Name], property)
                         ?? normalized;
            properties[property.Name] = result;
            _ = _processed.Add(result);
        }
    }

    JsonNode? Map(JsonNode original, Type contractType, string[] path, JsonPropertyInfo? property)
    {
        var mappingType = contractType;
        var representation = schemaType?.Invoke(mappingType);
        if (representation is null && Nullable.GetUnderlyingType(contractType) is { } underlying)
        {
            mappingType = underlying;
            representation = schemaType?.Invoke(mappingType);
        }
        if (representation is null || representation == mappingType)
            return null;

        var pointer = Pointer(path);
        if (_activeMappings.TryGetValue(mappingType, out var existing))
        {
            if (existing == pointer)
                throw new InvalidOperationException($"Cyclic OpenAPI schema type mapping for '{mappingType}'.");
            return ApplyAnnotations(new JsonObject { ["$ref"] = existing }, original, contractType, property);
        }

        _activeMappings.Add(mappingType, pointer);
        try
        {
            return ApplyAnnotations(Export(representation, path), original, contractType, property);
        }
        finally
        {
            _ = _activeMappings.Remove(mappingType);
        }
    }

    static JsonNode Normalize(JsonNode schema, Type type)
    {
        if (type == typeof(Uuid) || type == typeof(Uuid?))
        {
            var uuid = schema as JsonObject ?? [];
            uuid["type"] = type == typeof(Uuid?)
                ? new JsonArray(JsonValue.Create("string"), JsonValue.Create("null"))
                : JsonValue.Create("string");
            uuid["format"] = "uuid";
            return uuid;
        }
        // The OpenAPI reader expects object nodes for entries in a properties map.
        return schema.GetValueKind() switch
        {
            JsonValueKind.True => new JsonObject(),
            JsonValueKind.False => new JsonObject { ["not"] = new JsonObject() },
            _ => schema
        };
    }

    static JsonNode ApplyAnnotations(JsonNode mapped, JsonNode original, Type contractType, JsonPropertyInfo? property)
    {
        if (mapped is not JsonObject result)
            return mapped;
        var nullable = Nullable.GetUnderlyingType(contractType) is not null ||
                       (!contractType.IsValueType && (property is null || property.IsGetNullable || property.IsSetNullable));
        if (result["type"] is { } mappedType)
        {
            var names = mappedType is JsonArray types
                ? types.Select(item => item!.GetValue<string>()).Where(name => name != "null").ToList()
                : [mappedType.GetValue<string>()];
            if (nullable)
                names.Add("null");
            result["type"] = names.Count == 1
                ? JsonValue.Create(names[0])
                : new JsonArray(names.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        }
        else
        {
            result = nullable
                ? new JsonObject { ["anyOf"] = new JsonArray(result, new JsonObject { ["type"] = "null" }) }
                : new JsonObject
                {
                    ["allOf"] = new JsonArray(result,
                        new JsonObject { ["not"] = new JsonObject { ["type"] = "null" } })
                };
        }
        if (original is JsonObject originalObject && originalObject.ContainsKey("default"))
            result["default"] = originalObject["default"]?.DeepClone();
        return result;
    }

    static string Pointer(IEnumerable<string> path) => "#" + string.Concat(path.Select(segment =>
        "/" + segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)));
}
