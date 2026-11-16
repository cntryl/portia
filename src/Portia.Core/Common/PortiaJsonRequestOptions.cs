using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cntryl.Portia;

// Request deserialization and its schema share the generated binder's non-null constructor
// contract. These cached copies preserve application converters/naming and never alter output
// serialization or application event-schema evolution.
static class PortiaJsonRequestOptions
{
    static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> Cache = [];

    internal static JsonSerializerOptions Strict(JsonSerializerOptions source) => Cache.GetValue(source, static current =>
    {
        var options = new JsonSerializerOptions(current)
        {
            RespectNullableAnnotations = true,
            TypeInfoResolver = current.TypeInfoResolver!.WithAddedModifier(info => RequireNonNullableMembers(info, current))
        };
        options.MakeReadOnly();
        return options;
    });

    static void RequireNonNullableMembers(JsonTypeInfo info, JsonSerializerOptions facts)
    {
        foreach (var property in info.Properties)
        {
            if (PortiaJsonDescriptions.PropertyMetadata(facts, property)?.Input == false)
                continue;
            property.IsGetNullable = property.IsSetNullable;
            if (property.AssociatedParameter is { HasDefaultValue: false, IsNullable: false })
                property.IsRequired = true;
        }
    }
}
