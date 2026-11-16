using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cntryl.Portia;

/// <summary>Explicit descriptions for Portia's OpenAPI document. Configure before generating the document.</summary>
public sealed class PortiaOpenApiOptions
{
    readonly Dictionary<Type, PortiaOpenApiSchemaDescription> _schemas = [];
    readonly Dictionary<string, string> _members = [with(StringComparer.Ordinal)];

    /// <summary>Gets or sets whether document generation rejects missing operation, parameter, response, object schema or property descriptions.</summary>
    public bool RequireDescriptions { get; set; }

    /// <summary>Describes an exact contract type and, optionally, its direct CLR properties.</summary>
    public PortiaOpenApiOptions DescribeSchema<T>(string description,
        Action<PortiaOpenApiSchemaDescription<T>>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        var schema = new PortiaOpenApiSchemaDescription<T>();
        configure?.Invoke(schema);
        if (!_schemas.TryAdd(typeof(T), new PortiaOpenApiSchemaDescription(description, schema.Snapshot())))
            throw new InvalidOperationException($"OpenAPI schema '{typeof(T)}' is described more than once.");
        return this;
    }

    /// <summary>Describes every occurrence of an exact serialized property or HTTP parameter name unless overridden.</summary>
    public PortiaOpenApiOptions DescribeMember(string name, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (!_members.TryAdd(name, description))
            throw new InvalidOperationException($"OpenAPI member '{name}' is described more than once.");
        return this;
    }

    internal string? Schema(Type type, JsonSerializerOptions options) =>
        Registration(type)?.Description ?? PortiaJsonDescriptions.Type(options, type);

    internal string? Member(Type? type, string clrName, string wireName, JsonSerializerOptions options) =>
        (type is not null && Registration(type)?.Properties.TryGetValue(clrName, out var text) == true ? text : null)
        ?? PortiaJsonDescriptions.Member(options, type, clrName) ?? SharedMember(wireName);

    internal string? SharedMember(string wireName) => _members.GetValueOrDefault(wireName);

    internal string? Property(JsonPropertyInfo property, JsonSerializerOptions options,
        JsonSerializerOptions? descriptionOptions = null)
    {
        var name = DeclaredName(property);
        if (name is null && property.DeclaringType is { } declaringType && Registration(declaringType) is { } schema)
            name = schema.Properties.Keys.SingleOrDefault(candidate =>
                (options.PropertyNamingPolicy?.ConvertName(candidate) ?? candidate) == property.Name);
        return Member(property.DeclaringType, name ?? property.Name, property.Name, descriptionOptions ?? options);
    }

    internal void ValidateProperties(JsonTypeInfo typeInfo, IEnumerable<string> serializedNames)
    {
        if (Registration(typeInfo.Type) is not { } schema)
            return;
        var names = serializedNames.ToHashSet(StringComparer.Ordinal);
        foreach (var name in schema.Properties.Keys)
        {
            if (!typeInfo.Properties.Any(property => names.Contains(property.Name) && (
                    string.Equals(DeclaredName(property), name, StringComparison.OrdinalIgnoreCase) ||
                    (DeclaredName(property) is null && property.Name ==
                        (typeInfo.Options.PropertyNamingPolicy?.ConvertName(name) ?? name)))))
                throw new InvalidOperationException(
                    $"OpenAPI property '{typeInfo.Type}.{name}' is not present in its JSON contract.");
        }
    }

    static string? DeclaredName(JsonPropertyInfo property) =>
        property.AssociatedParameter?.Name ?? (property.AttributeProvider as MemberInfo)?.Name;

    PortiaOpenApiSchemaDescription? Registration(Type type) =>
        _schemas.GetValueOrDefault(type) ?? (Nullable.GetUnderlyingType(type) is { } underlying
            ? _schemas.GetValueOrDefault(underlying) : null);
}

sealed record PortiaOpenApiSchemaDescription(string Description, IReadOnlyDictionary<string, string> Properties);
