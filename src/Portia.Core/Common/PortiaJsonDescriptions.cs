using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Cntryl.Portia;

// The serializer resolver and its description literals are composed together per application.
// Copies of serializer options retain this resolver, without static registries or reflection scans.
sealed class PortiaJsonDescriptionResolver(
    IJsonTypeInfoResolver inner,
    PortiaContractMetadataRegistry contracts) : IJsonTypeInfoResolver
{
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => inner.GetTypeInfo(type, options);

    internal IReadOnlyList<PortiaResolvedExample> Examples(JsonSerializerOptions options, Type type, bool input) =>
        contracts.Examples.Resolve(options, type, input);

    internal PortiaContractMetadata? Metadata(Type type) => contracts.Type(type);

    internal PortiaContractMemberMetadata? Member(Type type, string name, JsonNamingPolicy? policy) =>
        contracts.Member(type, name, policy);

    internal PortiaContractMemberMetadata? WireMember(Type type, string name, JsonNamingPolicy? policy) =>
        contracts.WireMember(type, name, policy);

    internal string? Find(Type type, string? member, JsonNamingPolicy? policy) => member is null
        ? PortiaJsonDescriptions.Description(contracts.Type(type))
        : PortiaJsonDescriptions.Description(contracts.Member(type, member, policy));
}

static class PortiaJsonDescriptions
{
    internal static IReadOnlyList<PortiaResolvedExample>
        Examples(JsonSerializerOptions options, Type type, bool input) =>
        (options.TypeInfoResolver as PortiaJsonDescriptionResolver)?.Examples(options, type, input) ?? [];

    internal static PortiaContractMetadata? Metadata(JsonSerializerOptions options, Type type) =>
        (options.TypeInfoResolver as PortiaJsonDescriptionResolver)?.Metadata(type);

    internal static PortiaContractMemberMetadata?
        MemberMetadata(JsonSerializerOptions options, Type? type, string name) =>
        type is null
            ? null
            : (options.TypeInfoResolver as PortiaJsonDescriptionResolver)?.Member(type, name,
                options.PropertyNamingPolicy);

    internal static PortiaContractMemberMetadata? PropertyMetadata(
        JsonSerializerOptions options,
        JsonPropertyInfo property
    )
    {
        var declared = property.AssociatedParameter?.Name ?? (property.AttributeProvider as MemberInfo)?.Name;
        return declared is not null
            ? MemberMetadata(options, property.DeclaringType, declared)
            : property.DeclaringType is { } type && options.TypeInfoResolver is PortiaJsonDescriptionResolver resolver
                ? resolver.WireMember(type, property.Name, options.PropertyNamingPolicy)
                : null;
    }

    internal static string? Description(PortiaContractMetadata? metadata) =>
        CombineDescription(metadata?.Description, metadata?.Deprecated == true ? metadata.DeprecationMessage : null);

    internal static string? Description(PortiaContractMemberMetadata? metadata) =>
        CombineDescription(metadata?.Description, metadata?.Deprecated == true ? metadata.DeprecationMessage : null);

    static string? CombineDescription(string? description, string? deprecation) =>
        string.IsNullOrWhiteSpace(deprecation)
            ? description
            : string.IsNullOrWhiteSpace(description)
                ? deprecation
                : description + "\n\nDeprecated: " + deprecation;

    internal static void ApplyFacts(
        JsonObject schema,
        JsonSerializerOptions options,
        Type type,
        JsonPropertyInfo? property = null
    ) =>
        ApplyMemberFacts(schema, options, type, property is null ? null : PropertyMetadata(options, property));

    internal static void ApplyMemberFacts(
        JsonObject schema,
        JsonSerializerOptions options,
        Type type,
        PortiaContractMemberMetadata? member
    )
    {
        var contract = Metadata(options, type);
        var title = member?.Title ?? contract?.Title;

        if (title is not null)
            schema["title"] = title;

        if (member?.Deprecated == true || contract?.Deprecated == true)
            schema["deprecated"] = true;

        if (member is not null)
            PortiaJsonConstraints.Apply(schema, member, options);
    }

    internal static string? Type(JsonSerializerOptions options, Type type) =>
        Find(options, type, null) ?? (Nullable.GetUnderlyingType(type) is { } underlying
            ? Find(options, underlying, null)
            : null);

    internal static string? Member(JsonSerializerOptions options, Type? type, string member) =>
        type is null ? null : Find(options, type, member);

    internal static string? Property(JsonSerializerOptions options, JsonPropertyInfo property) =>
        Description(PropertyMetadata(options, property));

    static string? Find(JsonSerializerOptions options, Type type, string? member) =>
        (options.TypeInfoResolver as PortiaJsonDescriptionResolver)?.Find(type, member, options.PropertyNamingPolicy);

    internal static void ApplyExamples(JsonObject schema, JsonSerializerOptions options, Type type, bool input)
    {
        var examples = Examples(options, type, input);

        if (examples.Count > 0)
            schema["examples"] = new JsonArray(examples.Select(example => example.Value?.DeepClone()).ToArray());
    }

    internal static void CompleteRequired(JsonNode node, JsonTypeInfo info, JsonSerializerOptions options, bool input)
    {
        if (node is not JsonObject schema || schema["properties"] is not JsonObject properties)
            return;

        var required = schema["required"] as JsonArray ?? [];

        for (var index = required.Count - 1; index >= 0; index--)
        {
            var name = required[index]?.GetValue<string>();
            var property = info.Properties.FirstOrDefault(candidate => candidate.Name == name);
            var omit = property is null || (input
                ? !property.IsRequired && !PortiaJsonConstraints.RequiresPresence(PropertyMetadata(options, property))
                : property.ShouldSerialize is not null ||
                  options.DefaultIgnoreCondition == JsonIgnoreCondition.WhenWritingDefault ||
                  options.DefaultIgnoreCondition == JsonIgnoreCondition.WhenWritingNull &&
                  (!property.PropertyType.IsValueType ||
                   Nullable.GetUnderlyingType(property.PropertyType) is not null));

            if (omit)
                required.RemoveAt(index);
        }


        if (input)
        {
            foreach (var property in info.Properties)
                if (properties.ContainsKey(property.Name) &&
                    (property.IsRequired ||
                     PortiaJsonConstraints.RequiresPresence(PropertyMetadata(options, property))) &&
                    !required.Any(value => value?.GetValue<string>() == property.Name))
                    required.Add((JsonNode?)JsonValue.Create(property.Name));
        }

        if (input)
            AddConditions(schema, info, options, properties);

        if (required.Count == 0)
            _ = schema.Remove("required");
        else if (schema["required"] is null)
            schema["required"] = required;
    }

    internal static void ApplyDecimalString(JsonObject schema, Type type)
    {
        if (type != typeof(DecimalString) && type != typeof(DecimalString?))
            return;

        schema["type"] = type == typeof(DecimalString?)
            ? new JsonArray(JsonValue.Create("string"), JsonValue.Create("null"))
            : JsonValue.Create("string");
        schema["pattern"] = DecimalString.Pattern;
    }

    static void AddConditions(JsonObject schema, JsonTypeInfo info, JsonSerializerOptions options, JsonObject properties)
    {
        if (Metadata(options, info.Type) is not { } contract || !contract.Members.Any(member => member.RequiredWhen.Count != 0))
            return;

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in info.Properties)
        {
            var declared = PropertyMetadata(options, property)?.Name ?? property.AssociatedParameter?.Name ??
                           (property.AttributeProvider as MemberInfo)?.Name;

            if (properties.ContainsKey(property.Name) && declared is not null)
                names[declared] = property.Name;
        }


        var conditions = schema["allOf"] as JsonArray ?? [];

        foreach (var member in contract.Members)
        {
            foreach (var condition in member.RequiredWhen)
            {
                if (!names.TryGetValue(condition.Selector, out var selector) || !names.TryGetValue(member.Name, out var target))
                    throw new InvalidOperationException($"RequiredWhen requires JSON members '{condition.Selector}' and '{member.Name}'.");

                conditions.Add((JsonNode)new JsonObject
                {
                    ["if"] = new JsonObject
                    {
                        ["required"] = new JsonArray(JsonValue.Create(selector)),
                        ["properties"] = new JsonObject { [selector] = new JsonObject { ["const"] = condition.Value } }
                    },
                    ["then"] = new JsonObject
                    {
                        ["required"] = new JsonArray(JsonValue.Create(target)),
                        ["properties"] = new JsonObject
                        {
                            [target] = member.Type == typeof(string)
                                ? new JsonObject { ["type"] = "string", ["pattern"] = PortiaJsonConstraints.RequiredTextPattern }
                                : new JsonObject { ["not"] = new JsonObject { ["type"] = "null" } }
                        }
                    }
                });
            }
        }


        if (schema["allOf"] is null)
            schema["allOf"] = conditions;
    }

    internal static JsonNode Apply(
        JsonSchemaExporterContext context,
        JsonNode node,
        JsonSerializerOptions options,
        bool input = false
    )
    {
        node = node.GetValueKind() switch
        {
            JsonValueKind.True => new JsonObject(),
            JsonValueKind.False => new JsonObject { ["not"] = new JsonObject() },
            _ => node
        };
        var isProperty = context.PropertyInfo is { } property && context.Path.Length >= 2 &&
                         context.Path[^2] == "properties" && context.Path[^1] == property.Name;
        var text = (isProperty ? Property(options, context.PropertyInfo!) : null) ??
                   Type(options, context.TypeInfo.Type);

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


        if (node is JsonObject schemaNode)
        {
            ApplyDecimalString(schemaNode, isProperty ? context.PropertyInfo!.PropertyType : context.TypeInfo.Type);
            ApplyFacts(schemaNode, options, context.TypeInfo.Type, isProperty ? context.PropertyInfo : null);
        }
        // Some defaulted properties do not receive an exporter callback.
        if (node is JsonObject obj && obj["properties"] is JsonObject properties)
        {
            foreach (var child in context.TypeInfo.Properties)
            {
                if (properties[child.Name] is not JsonObject childSchema)
                    continue;

                ApplyDecimalString(childSchema, child.PropertyType);
                ApplyFacts(childSchema, options, child.PropertyType, child);

                if (Property(options, child) is { } childText)
                    childSchema["description"] = childText;
            }
        }


        if (node is JsonObject exampleSchema)
            ApplyExamples(exampleSchema, options, context.TypeInfo.Type, input);

        CompleteRequired(node, context.TypeInfo, options, input);
        return node;
    }
}
