using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Cntryl.Portia;

// Export the actual Portia contract, independently of application-owned ASP.NET JSON options.
static class PortiaOpenApiSchemaGenerator
{
    public static IOpenApiSchema Create(Type type, JsonSerializerOptions options, OpenApiDocument document)
    {
        if (typeof(Stream).IsAssignableFrom(type))
            return new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
        var node = options.GetTypeInfo(type).GetJsonSchemaAsNode(new JsonSchemaExporterOptions
        {
            // The OpenAPI reader expects object nodes for entries in a properties map.
            TransformSchemaNode = (context, schema) => context.TypeInfo.Type == typeof(Uuid)
                ? new JsonObject { ["type"] = "string", ["format"] = "uuid" }
                : schema.GetValueKind() switch
                {
                    JsonValueKind.True => new JsonObject(),
                    JsonValueKind.False => new JsonObject { ["not"] = new JsonObject() },
                    _ => schema
                }
        });
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
    }
}
