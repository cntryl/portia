using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Binding primitives used by generated Portia HTTP endpoints.</summary>
public static class PortiaHttpBinding
{
    /// <summary>Creates execution context from authenticated HTTP state and concrete endpoint facts.</summary>
    public static RequestDispatchContext CreateDispatchContext(HttpContext context)
        => new(context.User, new HttpInvocation(context.Request.Method,
            context.Request.PathBase.Add(context.Request.Path).Value ?? "/",
            (context.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern.RawText,
            context.TraceIdentifier), timeProvider: context.RequestServices.GetService<TimeProvider>());

    /// <summary>Gets the application's frozen Portia JSON options.</summary>
    public static JsonSerializerOptions GetJsonOptions(HttpContext context)
        => context.RequestServices.GetRequiredService<JsonSerializerOptions>();

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

    /// <summary>Reads a constructor-bound property using the application's request JSON metadata.</summary>
    public static TValue ReadBody<TRequest, TValue>(JsonElement body, JsonSerializerOptions options, int memberIndex, string memberName,
        string fallbackName, bool nullable, bool hasDefault, TValue defaultValue)
    {
        _ = memberName;
        JsonPropertyInfo? property = null;
        try
        {
            var properties = options.GetTypeInfo(typeof(TRequest)).Properties;
            property = properties.FirstOrDefault(candidate => candidate.Name == fallbackName)
                ?? properties.ElementAtOrDefault(memberIndex);
        }
        catch (NotSupportedException)
        {
            // Compile-time generated bindings can read scalar members without constructing the
            // request through JSON. A request-level contract is still required by transported roots.
        }
        if (property?.CustomConverter is not null || property?.NumberHandling is not null)
        {
            options = new JsonSerializerOptions(options);
            if (property.CustomConverter is not null)
                options.Converters.Insert(0, property.CustomConverter);
            if (property.NumberHandling is { } numberHandling)
                options.NumberHandling = numberHandling;
        }
        return ReadBody(body, options, property?.Name ?? fallbackName, nullable, hasDefault, defaultValue);
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
        var typeInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        var result = JsonSerializer.Deserialize(value, typeInfo);
        return result is null && !nullable
            ? throw new BadHttpRequestException($"Property '{name}' cannot be null.")
            : result!;
    }
}
