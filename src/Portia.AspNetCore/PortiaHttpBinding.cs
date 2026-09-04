using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cntryl.Portia;

/// <summary>Binding primitives used by generated Portia HTTP endpoints.</summary>
public static class PortiaHttpBinding
{
    /// <summary>Gets the application's configured ASP.NET HTTP JSON options.</summary>
    public static JsonSerializerOptions GetJsonOptions(HttpContext context)
        => context.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

    /// <summary>Resolves contextual queue route values configured on the selected endpoint.</summary>
    public static RequestRouteValues ResolveRouteValues(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<PortiaHttpRouteValues>();
        return metadata is null ? RequestRouteValues.None
            : metadata.Resolve(context) ?? throw new InvalidOperationException("The endpoint route-values resolver returned null.");
    }

    /// <summary>Reads one query value, distinguishing omission from an empty string.</summary>
    public static string? ReadQuery(HttpContext context, string name)
    {
        return !context.Request.Query.TryGetValue(name, out var values)
            ? null
            : values.Count == 1 ? values[0] : throw new BadHttpRequestException($"Expected one value for '{name}'.");
    }

    /// <summary>Reads a body property with the configured naming, converters and null contract.</summary>
    public static T ReadBody<T>(JsonElement body, JsonSerializerOptions options, string name, bool nullable, bool hasDefault, T defaultValue)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw new BadHttpRequestException("Expected a JSON object.");
        var found = body.TryGetProperty(name, out var value);
        if (!found && options.PropertyNameCaseInsensitive)
        {
            foreach (var property in body.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    found = true;
                }
            }
        }
        if (!found)
        {
            return hasDefault || nullable ? defaultValue
                : throw new BadHttpRequestException($"Missing required property '{name}'.");
        }
        if (value.ValueKind == JsonValueKind.Null && !nullable)
            throw new BadHttpRequestException($"Property '{name}' cannot be null.");
        var result = JsonSerializer.Deserialize<T>(value, options);
        return result is null && !nullable
            ? throw new BadHttpRequestException($"Property '{name}' cannot be null.")
            : result!;
    }
}
