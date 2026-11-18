using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Cntryl.Portia;

// Export the actual Portia contract, independently of application-owned ASP.NET JSON options.
static class PortiaOpenApiSchemaGenerator
{
    static readonly string[] EditorialKeywords =
    [
        "description", "title", "examples", "example", "default", "deprecated", "externalDocs", "$comment", "readOnly",
        "writeOnly"
    ];

    public static IOpenApiSchema Create(Type type, JsonSerializerOptions options, OpenApiDocument document,
        Func<Type, Type?>? schemaType = null, bool rootIsNullable = false,
        Func<Type, Type, bool?>? schemaCollectionElementIsNullable = null,
        PortiaOpenApiOptions? descriptions = null, PortiaContractMemberMetadata? member = null, bool input = false)
    {
        if (typeof(Stream).IsAssignableFrom(type))
            return new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
        var node = new PortiaOpenApiJsonSchemaExporter(options, schemaType, schemaCollectionElementIsNullable,
                descriptions, input)
            .Export(type, rootIsNullable);
        JsonObject? originalAnnotations = null;
        JsonObject? memberAnnotations = null;
        if (node is JsonObject root && member is not null)
        {
            originalAnnotations = Annotations(root);
            PortiaJsonDescriptions.ApplyMemberFacts(root, options, type, member);
            memberAnnotations = Annotations(root);
        }

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

        // A member's editorial facts are siblings of its reference. They must not become
        // the defaults of a reusable component, even though they do not affect its identity.
        if (originalAnnotations is not null && node is JsonObject component)
        {
            foreach (var keyword in EditorialKeywords)
                _ = component.Remove(keyword);
            foreach (var (keyword, value) in originalAnnotations)
                component[keyword] = value?.DeepClone();
        }

        var shape = node.DeepClone();
        RemoveAnnotations(shape);
        // Direction is part of the contract: even identical shapes can have different nested examples.
        var identity = type.Assembly.GetName().Name + "\n" + type.FullName + "\n" + (input ? "input" : "output") +
                       "\n" + shape.ToJsonString();
        var name = string.Concat(FriendlyName(type).Select(character =>
            char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_'));
        var id = "Portia_" + name + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        if (document.Components?.Schemas?.ContainsKey(id) == true)
            return Reference(id);

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
        return Reference(id);

        OpenApiSchemaReference Reference(string componentId)
        {
            var reference = new OpenApiSchemaReference(componentId, document);
            if (memberAnnotations is null)
                return reference;
            var annotations = Read(memberAnnotations);
            if (memberAnnotations.ContainsKey("title"))
                reference.Reference.Title = annotations.Title;
            if (memberAnnotations.ContainsKey("description"))
                reference.Description = annotations.Description;
            if (memberAnnotations.ContainsKey("default"))
                reference.Reference.Default = annotations.Default;
            if (memberAnnotations.ContainsKey("deprecated"))
                reference.Reference.Deprecated = annotations.Deprecated;
            if (memberAnnotations.ContainsKey("readOnly"))
                reference.Reference.ReadOnly = annotations.ReadOnly;
            if (memberAnnotations.ContainsKey("writeOnly"))
                reference.Reference.WriteOnly = annotations.WriteOnly;
            if (memberAnnotations.ContainsKey("examples"))
                reference.Reference.Examples = annotations.Examples;
            return reference;
        }

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
            if (current is not JsonObject obj)
                return;
            if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var target) &&
                target.StartsWith('#'))
                references.Add((obj, target));
            foreach (var (name, child) in SchemaChildren(obj))
                Visit(child, path + "/" + name);
        }
    }

    static string FriendlyName(Type type)
    {
        if (type.IsArray)
            return FriendlyName(type.GetElementType()!) + "Array";
        var name = type.Name.Split('`')[0];
        return type.IsGenericType
            ? name + "Of" + string.Join("And", type.GetGenericArguments().Select(FriendlyName))
            : name;
    }

    static JsonObject Annotations(JsonObject schema)
    {
        var annotations = new JsonObject();
        foreach (var keyword in EditorialKeywords)
            if (schema.TryGetPropertyValue(keyword, out var value))
                annotations[keyword] = value?.DeepClone();
        return annotations;
    }

    static void RemoveAnnotations(JsonNode node)
    {
        if (node is not JsonObject schema)
            return;
        foreach (var name in EditorialKeywords)
            _ = schema.Remove(name);
        foreach (var (_, child) in SchemaChildren(schema))
            RemoveAnnotations(child);
    }

    static IEnumerable<(string Path, JsonNode Schema)> SchemaChildren(JsonObject schema)
    {
        foreach (var (name, node) in schema)
        {
            if (node is JsonObject map && name is "properties" or "patternProperties" or "$defs" or "dependentSchemas")
            {
                foreach (var (key, child) in map)
                    if (child is not null)
                        yield return (
                            name + "/" + key.Replace("~", "~0", StringComparison.Ordinal)
                                .Replace("/", "~1", StringComparison.Ordinal), child);
            }
            else if (node is JsonArray array && name is "allOf" or "anyOf" or "oneOf" or "prefixItems")
            {
                for (var index = 0; index < array.Count; index++)
                    if (array[index] is { } child)
                        yield return (name + "/" + index.ToString(CultureInfo.InvariantCulture), child);
            }
            else if (node is not null && name is "items" or "additionalProperties" or "unevaluatedProperties"
                         or "propertyNames" or "contains" or "not" or "if" or "then" or "else")
                yield return (name, node);
        }
    }
}
