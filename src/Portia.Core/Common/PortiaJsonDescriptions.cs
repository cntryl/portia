using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Cntryl.Portia;

// The serializer resolver and its description literals are composed together per application.
// Copies of serializer options retain this resolver, without static registries or reflection scans.
sealed class PortiaJsonDescriptionResolver(IJsonTypeInfoResolver inner,
    IReadOnlyList<Func<Type, string?, JsonNamingPolicy?, string?>> descriptions) : IJsonTypeInfoResolver
{
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => inner.GetTypeInfo(type, options);

    internal string? Find(Type type, string? member, JsonNamingPolicy? policy)
    {
        foreach (var describe in descriptions)
            if (describe(type, member, policy) is { } text)
                return text;
        return null;
    }
}

static class PortiaJsonDescriptions
{
    internal static string? Type(JsonSerializerOptions options, Type type) =>
        Find(options, type, null) ?? (Nullable.GetUnderlyingType(type) is { } underlying
            ? Find(options, underlying, null) : null);

    internal static string? Member(JsonSerializerOptions options, Type? type, string member) =>
        type is null ? null : Find(options, type, member);

    internal static string? Property(JsonSerializerOptions options, JsonPropertyInfo property) =>
        Member(options, property.DeclaringType,
            property.AssociatedParameter?.Name ?? (property.AttributeProvider as MemberInfo)?.Name ?? property.Name);

    static string? Find(JsonSerializerOptions options, Type type, string? member) =>
        (options.TypeInfoResolver as PortiaJsonDescriptionResolver)?.Find(type, member, options.PropertyNamingPolicy);

    internal static JsonNode Apply(JsonSchemaExporterContext context, JsonNode node, JsonSerializerOptions options)
    {
        var isProperty = context.PropertyInfo is { } property && context.Path.Length >= 2 &&
                         context.Path[^2] == "properties" && context.Path[^1] == property.Name;
        var text = (isProperty ? Property(options, context.PropertyInfo!) : null) ?? Type(options, context.TypeInfo.Type);
        if (text is not null)
        {
            node = node.GetValueKind() switch
            {
                JsonValueKind.True => new JsonObject(),
                JsonValueKind.False => new JsonObject { ["not"] = new JsonObject() },
                _ => node
            };
            if (node is JsonObject described)
                described["description"] = text;
        }
        // Some defaulted properties do not receive an exporter callback.
        if (node is JsonObject obj && obj["properties"] is JsonObject properties)
        {
            foreach (var child in context.TypeInfo.Properties)
                if (properties[child.Name] is JsonObject childSchema && Property(options, child) is { } childText)
                    childSchema["description"] = childText;
        }
        return node;
    }
}
