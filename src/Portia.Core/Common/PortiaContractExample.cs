using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cntryl.Portia;

sealed record PortiaContractExample(Type Type, string Name, object Value, string? Summary, PortiaContractUse Use);
sealed record PortiaResolvedExample(string Name, string? Summary, JsonNode? Value, IReadOnlyDictionary<string, object?>? Members = null);

sealed class PortiaContractExamples(IReadOnlyList<PortiaContractExample> examples, PortiaContractMetadataRegistry contracts)
{
    readonly ConditionalWeakTable<JsonSerializerOptions, ExampleCache> _cache = [];

    internal IReadOnlyList<PortiaResolvedExample> Resolve(JsonSerializerOptions json, Type type, bool input) =>
        _cache.GetValue(json, static _ => new ExampleCache()).Resolve((type, input), () =>
            examples.Where(example => example.Type == type && example.Use.HasFlag(input ? PortiaContractUse.Input : PortiaContractUse.Output))
                .Select(example => Resolve(json, example, input)).ToArray());

    internal static PortiaResolvedExample Resolve(JsonSerializerOptions json, PortiaContractMetadataRegistry contracts,
        PortiaContractExample example, bool input)
    {
        try
        {
            var error = PortiaContractValidation.Validate(contracts, example.Value, CancellationToken.None,
                example.Type, input ? PortiaContractUse.Input : PortiaContractUse.Output);
            if (error is not null)
                throw new InvalidOperationException(error.Message);
            var options = PortiaJsonSchemaProjection.For(json, json, input);
            var info = options.GetTypeInfo(example.Type);
            var value = JsonSerializer.SerializeToNode(example.Value, info);
            // Snapshot input CLR values for adapters whose scalar binding intentionally bypasses
            // JSON member converters. Keep the JSON illustration and parameter values in one cache.
            var members = input && info.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object
                ? info.Properties.Where(property => property.Get is not null).ToDictionary(property => property.Name,
                    property => property.Get!(example.Value), StringComparer.Ordinal)
                : null;
            return new PortiaResolvedExample(example.Name, example.Summary, value, members);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Contract example '{example.Name}' for '{example.Type}' is invalid: {error.Message}", error);
        }
    }

    PortiaResolvedExample Resolve(JsonSerializerOptions json, PortiaContractExample example, bool input) =>
        Resolve(json, contracts, example, input);

    sealed class ExampleCache
    {
        readonly Lock _gate = new();
        readonly Dictionary<(Type Type, bool Input), IReadOnlyList<PortiaResolvedExample>> _values = [];

        internal IReadOnlyList<PortiaResolvedExample> Resolve((Type Type, bool Input) key, Func<IReadOnlyList<PortiaResolvedExample>> create)
        {
            lock (_gate)
            {
                if (!_values.TryGetValue(key, out var value))
                    _values.Add(key, value = create());
                return value;
            }
        }
    }
}
