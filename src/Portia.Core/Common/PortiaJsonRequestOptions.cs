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

    internal static JsonSerializerOptions Strict(JsonSerializerOptions source) => Cache.GetValue(source,
        static current =>
        {
            var options = new JsonSerializerOptions(current)
            {
                RespectNullableAnnotations = true,
                RespectRequiredConstructorParameters = true,
                TypeInfoResolver =
                    current.TypeInfoResolver!.WithAddedModifier(info => RequireNonNullableMembers(info, current))
            };
            options.MakeReadOnly();
            return options;
        });

    static void RequireNonNullableMembers(JsonTypeInfo info, JsonSerializerOptions facts)
    {
        JsonTypeInfo? original = null;

        foreach (var property in info.Properties)
        {
            if (PortiaJsonDescriptions.PropertyMetadata(facts, property)?.Input == false)
            {
                property.IsRequired = false;
                continue;
            }

            property.IsGetNullable = property.IsSetNullable;

            if (property.AssociatedParameter is { HasDefaultValue: false, IsMemberInitializer: false } parameter)
            {
                if (!parameter.IsNullable || facts.RespectRequiredConstructorParameters)
                    property.IsRequired = true;
                else
                {
                    // Constructor policy makes getter-only members legal required inputs. Retain the
                    // application's nullable omission semantics and explicit JsonRequired/modifier facts.
                    // Ask the resolver for raw metadata so an unconfigured getter-only JsonRequired
                    // contract is not configured using the original, less strict constructor options.
                    original ??= facts.TypeInfoResolver!.GetTypeInfo(info.Type, facts);
                    property.IsRequired = original?.Properties
                        .SingleOrDefault(candidate => candidate.Name == property.Name)?.IsRequired == true;
                }
            }
        }
    }
}
