using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Cntryl.Portia;

// Schema-only copies retain the application's converters and naming while projecting the
// serializer's input/output access. The actual wire serializer options remain immutable.
static class PortiaJsonSchemaProjection
{
    static readonly ConditionalWeakTable<JsonSerializerOptions, Projections> Cache = [];

    internal static JsonSerializerOptions For(JsonSerializerOptions source, JsonSerializerOptions facts, bool input) =>
        Cache.GetValue(source, current => new Projections(current, facts)).Get(input);

    sealed class Projections(JsonSerializerOptions source, JsonSerializerOptions facts)
    {
        readonly Lazy<JsonSerializerOptions> _input = new(() => Create(source, facts, true));
        readonly Lazy<JsonSerializerOptions> _output = new(() => Create(source, facts, false));
        internal JsonSerializerOptions Get(bool input) => input ? _input.Value : _output.Value;
    }

    static JsonSerializerOptions Create(JsonSerializerOptions source, JsonSerializerOptions facts, bool input)
    {
        var contract = input ? PortiaJsonRequestOptions.Strict(source) : source;
        var options = new JsonSerializerOptions(contract)
        {
            TypeInfoResolver = contract.TypeInfoResolver!.WithAddedModifier(info =>
            {
                if (info.Kind != JsonTypeInfoKind.Object)
                    return;
                for (var index = info.Properties.Count - 1; index >= 0; index--)
                {
                    var property = info.Properties[index];
                    var member = PortiaJsonDescriptions.PropertyMetadata(facts, property);
                    var handling = property.ObjectCreationHandling ?? info.PreferredPropertyObjectCreationHandling ?? source.PreferredObjectCreationHandling;
                    var permitted = input
                        ? member?.Input != false && (property.Set is not null || property.AssociatedParameter is not null ||
                            handling == JsonObjectCreationHandling.Populate)
                        : member?.Output != false && property.Get is not null;
                    if (!permitted)
                    {
                        info.Properties.RemoveAt(index);
                        continue;
                    }
                    if (input)
                    {
                        property.ShouldSerialize = null;
                        property.IsGetNullable = property.IsSetNullable;
                        property.IsRequired |= PortiaJsonConstraints.RequiresPresence(member);
                    }
                }
            })
        };
        options.MakeReadOnly();
        return options;
    }
}
