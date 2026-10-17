using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Cntryl.Portia;

// Export the actual Portia contract, independently of application-owned ASP.NET JSON options.
static class PortiaOpenApiSchemaGenerator
{
    public static IOpenApiSchema Create(Type type, JsonSerializerOptions options, OpenApiDocument document,
        Func<Type, Type?>? schemaType = null)
    {
        if (typeof(Stream).IsAssignableFrom(type))
            return new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
        var activeMappings = new Dictionary<Type, string>();
        var node = Export(type, []);
        var nodes = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var references = new List<(JsonObject Node, string Target)>();
        Visit(node, "#");

        // Scalars stay inline so parameter defaults remain local to each operation.
        if (references.Count == 0 && node is not JsonObject)
            return Read(node);
        if (references.Count == 0 && node is JsonObject scalar &&
            !scalar.ContainsKey("properties") && !scalar.ContainsKey("items") &&
            !scalar.ContainsKey("anyOf") && !scalar.ContainsKey("oneOf"))
            return Read(node);

        var id = "Portia_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString())));
        if (document.Components?.Schemas?.ContainsKey(id) == true)
            return new OpenApiSchemaReference(id, document);

        // The exporter uses root-relative JSON pointers, including pointers into nested properties.
        // Hoist each referenced target so the OpenAPI reader can resolve it as a component.
        var targets = new Dictionary<string, string>(StringComparer.Ordinal) { ["#"] = id };
        foreach (var (_, target) in references)
        {
            if (!targets.ContainsKey(target))
                targets.Add(target, id + "_" + targets.Count.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var (reference, target) in references)
            reference["$ref"] = "#/components/schemas/" + targets[target];
        foreach (var (target, componentId) in targets)
            _ = document.AddComponent(componentId, Read(nodes[target]));
        return new OpenApiSchemaReference(id, document);

        IOpenApiSchema Read(JsonNode schema)
        {
            var result = new OpenApiJsonReader().ReadFragment<OpenApiSchema>(schema,
                OpenApiSpecVersion.OpenApi3_1, document, out var diagnostic);
            if (diagnostic.Errors.Count > 0)
                throw new InvalidOperationException(
                    $"Cannot export the Portia JSON schema for '{type}': {string.Join("; ", diagnostic.Errors)}");
            return result ?? throw new InvalidOperationException($"No Portia JSON schema was exported for '{type}'.");
        }

        void Visit(JsonNode current, string path)
        {
            nodes.Add(path, current);
            if (current is JsonObject obj)
            {
                if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var target) &&
                    target.StartsWith('#'))
                    references.Add((obj, target));
                foreach (var (name, child) in obj)
                {
                    if (child is not null)
                        Visit(child,
                            path + "/" + name.Replace("~", "~0", StringComparison.Ordinal)
                                .Replace("/", "~1", StringComparison.Ordinal));
                }
            }
            else if (current is JsonArray array)
            {
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is { } child)
                        Visit(child, path + "/" + index.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        JsonNode Export(Type contractType, string[] prefix)
        {
            var exported = options.GetTypeInfo(contractType).GetJsonSchemaAsNode(new JsonSchemaExporterOptions
            {
                TransformSchemaNode = (context, schema) =>
                {
                    var currentType = context.PropertyInfo?.PropertyType ?? context.TypeInfo.Type;
                    var normalized = currentType == typeof(Uuid) || currentType == typeof(Uuid?)
                        ? UuidSchema(schema, currentType == typeof(Uuid?))
                        : schema.GetValueKind() switch
                        {
                            JsonValueKind.True => new JsonObject(),
                            JsonValueKind.False => new JsonObject { ["not"] = new JsonObject() },
                            _ => schema
                        };
                    if (normalized is JsonObject reference && reference["$ref"] is JsonValue value &&
                        value.TryGetValue<string>(out var target) && target.StartsWith('#'))
                        reference["$ref"] = Pointer(prefix) + target[1..];
                    return normalized;
                }
            });
            return ApplyContractSchemas(exported, contractType, prefix);
        }

        static string Pointer(IEnumerable<string> path) => "#" + string.Concat(path.Select(segment =>
            "/" + segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)));

        static JsonNode PropertySchema(JsonNode mapped, JsonNode original, JsonPropertyInfo? property)
        {
            if (property is null || mapped is not JsonObject mappedObject)
                return mapped;
            var nullable = property.IsGetNullable || property.IsSetNullable ||
                           Nullable.GetUnderlyingType(property.PropertyType) is not null;
            if (mappedObject["type"] is { } mappedType)
            {
                var names = mappedType is JsonArray types
                    ? types.Select(item => item!.GetValue<string>()).Where(name => name != "null").ToList()
                    : [mappedType.GetValue<string>()];
                if (nullable)
                    names.Add("null");
                mappedObject["type"] = names.Count == 1
                    ? JsonValue.Create(names[0])
                    : new JsonArray(names.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
            }
            else
            {
                mappedObject = nullable
                    ? new JsonObject { ["anyOf"] = new JsonArray(mappedObject, new JsonObject { ["type"] = "null" }) }
                    : new JsonObject
                    {
                        ["allOf"] = new JsonArray(mappedObject,
                            new JsonObject { ["not"] = new JsonObject { ["type"] = "null" } })
                    };
            }
            if (original is JsonObject originalObject && originalObject.ContainsKey("default"))
                mappedObject["default"] = originalObject["default"]?.DeepClone();
            return mappedObject;
        }

        static JsonObject UuidSchema(JsonNode schema, bool nullable)
        {
            // Preserve exporter-owned keywords such as a constructor parameter's default. The
            // property context is also authoritative for nullable value types: TypeInfo can be the
            // underlying Uuid when an optional record parameter defaults to null.
            var result = schema as JsonObject ?? [];
            result["type"] = nullable
                ? new JsonArray(JsonValue.Create("string"), JsonValue.Create("null"))
                : JsonValue.Create("string");
            result["format"] = "uuid";
            return result;
        }

        JsonNode ApplyContractSchemas(JsonNode schema, Type contractType, string[] path, JsonPropertyInfo? property = null)
        {
            var representation = schemaType?.Invoke(contractType);
            if (representation is not null && representation != contractType)
            {
                var pointer = Pointer(path);
                if (activeMappings.TryGetValue(contractType, out var existing))
                {
                    if (existing == pointer)
                        throw new InvalidOperationException($"Cyclic OpenAPI schema type mapping for '{contractType}'.");
                    return PropertySchema(new JsonObject { ["$ref"] = existing }, schema, property);
                }
                activeMappings.Add(contractType, pointer);
                try
                {
                    return PropertySchema(Export(representation, path), schema, property);
                }
                finally
                {
                    _ = activeMappings.Remove(contractType);
                }
            }
            if (schema is not JsonObject schemaObject)
            {
                return contractType == typeof(Uuid) || contractType == typeof(Uuid?)
                    ? UuidSchema(schema, contractType == typeof(Uuid?))
                    : schema;
            }

            if (contractType == typeof(Uuid) || contractType == typeof(Uuid?))
                return UuidSchema(schemaObject, contractType == typeof(Uuid?));

            if (schemaObject.ContainsKey("$ref"))
                return schemaObject;

            if (options.GetTypeInfo(contractType).ElementType is { } elementType)
            {
                schemaObject["items"] = ApplyContractSchemas(
                    schemaObject["items"] ?? new JsonObject(), elementType, [.. path, "items"]);
                return schemaObject;
            }

            if (schemaObject["properties"] is not JsonObject properties)
                return schemaObject;

            var typeInfo = options.GetTypeInfo(contractType);
            foreach (var member in typeInfo.Properties)
            {
                if (properties[member.Name] is not { } propertySchema)
                    continue;
                properties[member.Name] = ApplyContractSchemas(propertySchema, member.PropertyType, [.. path, "properties", member.Name], member);
            }
            return schemaObject;
        }

    }
}
