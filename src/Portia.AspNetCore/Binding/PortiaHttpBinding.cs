using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Binding primitives called by Portia's generated endpoint code. Public because the generated
///     code lives in the consuming assembly, not because these members are meant to be called
///     directly. Map endpoints with <see cref="PortiaEndpointRouteBuilderExtensions" /> instead.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static partial class PortiaHttpBinding
{
    static readonly ConditionalWeakTable<JsonSerializerOptions, OptionsBindingCache> BindingCaches = [];

    /// <summary>Creates execution context from authenticated HTTP state and concrete endpoint facts.</summary>
    /// <param name="context">The current HTTP request.</param>
    /// <returns>Execution state carrying the caller's principal and the endpoint's ingress facts.</returns>
    public static RequestDispatchContext CreateDispatchContext(HttpContext context)
        => new(context.User, new HttpInvocation(context.Request.Method,
            context.Request.PathBase.Add(context.Request.Path).Value ?? "/",
            (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
            context.TraceIdentifier), timeProvider: context.RequestServices.GetService<TimeProvider>());

    /// <summary>Gets the application's frozen Portia JSON options.</summary>
    /// <param name="context">The current HTTP request.</param>
    /// <returns>The options registered by <c>AddPortia</c>.</returns>
    public static JsonSerializerOptions GetJsonOptions(HttpContext context)
        => context.RequestServices.GetRequiredService<JsonSerializerOptions>();

    /// <summary>Ensures <c>AddHttp()</c> registered Portia's HTTP services before an endpoint is mapped.</summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <exception cref="InvalidOperationException"><c>AddHttp()</c> was not called.</exception>
    public static void RequireHttpServices(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.ServiceProvider.GetService<PortiaHttpExtensions.PortiaOpenApiMarker>() is null)
            throw new InvalidOperationException(
                "Portia HTTP endpoints require AddHttp() in the shared Portia application composition.");
    }

    /// <summary>Reports whether an exact RFC 7240 preference token requests asynchronous handling.</summary>
    /// <param name="context">The current HTTP request.</param>
    /// <returns><see langword="true" /> when the caller sent <c>Prefer: respond-async</c>.</returns>
    public static bool PrefersRespondAsync(HttpContext context)
    {
        // Every mutating request asks this, so the header is matched over spans: splitting it
        // allocated an array per value and a string per token to answer one boolean.
        foreach (var header in context.Request.Headers["Prefer"])
        {
            for (var remaining = header.AsSpan(); !remaining.IsEmpty;)
            {
                var separator = remaining.IndexOf(',');
                var preference = separator < 0 ? remaining : remaining[..separator];
                remaining = separator < 0 ? default : remaining[(separator + 1)..];
                var parameters = preference.IndexOf(';');
                if (parameters >= 0)
                    preference = preference[..parameters];
                if (preference.Trim().Equals("respond-async", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Resolves contextual queue route values configured on the selected endpoint.</summary>
    /// <param name="context">The current HTTP request.</param>
    /// <returns>
    ///     The values supplied by <see cref="PortiaEndpointRouteBuilderExtensions.WithPortiaRouteValues" />,
    ///     or <see cref="RequestRouteValues.None" /> when the endpoint configured none.
    /// </returns>
    public static RequestRouteValues ResolveRouteValues(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<PortiaHttpRouteValues>();
        return metadata is null
            ? RequestRouteValues.None
            : metadata.Resolve(context) ??
              throw new InvalidOperationException("The endpoint route-values resolver returned null.");
    }

    /// <summary>Reads one query value, distinguishing omission from an empty string.</summary>
    /// <param name="context">The current HTTP request.</param>
    /// <param name="name">The query parameter to read.</param>
    /// <returns>The single value, or <see langword="null" /> when the parameter was not sent.</returns>
    /// <exception cref="Microsoft.AspNetCore.Http.BadHttpRequestException">The parameter was sent more than once.</exception>
    public static string? ReadQuery(HttpContext context, string name)
    {
        return !context.Request.Query.TryGetValue(name, out var values)
            ? null
            : values.Count == 1
                ? values[0]
                : throw new BadHttpRequestException($"Expected one value for '{name}'.");
    }

    /// <summary>Reads a constructor-bound property using the application's request JSON metadata.</summary>
    /// <typeparam name="TRequest">The request type whose JSON contract names the member.</typeparam>
    /// <typeparam name="TValue">The member's type.</typeparam>
    /// <param name="body">The parsed request body.</param>
    /// <param name="options">The application's frozen Portia JSON options.</param>
    /// <param name="memberIndex">The member's position in the request's primary constructor.</param>
    /// <param name="memberName">The member's declared name. Reserved; the name is resolved from JSON metadata.</param>
    /// <param name="fallbackName">The wire name to use when JSON metadata is unavailable.</param>
    /// <param name="nullable">Whether an explicit JSON null is a legal value.</param>
    /// <param name="hasDefault">Whether the member has a default that applies when the property is absent.</param>
    /// <param name="defaultValue">The value used when the property is absent or null and that is allowed.</param>
    /// <returns>The bound member value.</returns>
    public static TValue ReadBody<TRequest, TValue>(JsonElement body, JsonSerializerOptions options, int memberIndex,
        string memberName,
        string fallbackName, bool nullable, bool hasDefault, TValue defaultValue)
    {
        _ = memberName;
        var binding = BindingCaches.GetValue(options, static current => new OptionsBindingCache(current))
            .Get(typeof(TRequest), typeof(TValue), memberIndex, fallbackName);
        return ReadBody(body, binding.Options, binding.Name, nullable, hasDefault, defaultValue);
    }

    /// <summary>Reads a body property with the configured naming, converters and null contract.</summary>
    /// <typeparam name="T">The property's type.</typeparam>
    /// <param name="body">The parsed request body.</param>
    /// <param name="options">The application's frozen Portia JSON options.</param>
    /// <param name="name">The wire name of the property to read.</param>
    /// <param name="nullable">Whether an explicit JSON null is a legal value.</param>
    /// <param name="hasDefault">Whether the property has a default that applies when it is absent.</param>
    /// <param name="defaultValue">The value used when the property is absent or null and that is allowed.</param>
    /// <returns>The bound property value.</returns>
    /// <exception cref="Microsoft.AspNetCore.Http.BadHttpRequestException">
    ///     The body is not a JSON object, or the property is missing or null where that is not allowed.
    /// </exception>
    public static T ReadBody<T>(JsonElement body, JsonSerializerOptions options, string name, bool nullable,
        bool hasDefault, T defaultValue)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new BadHttpRequestException("Expected a JSON object.");
        }

        if (!TryGetBodyProperty(body, options, name, out var value))
        {
            return hasDefault || nullable
                ? defaultValue
                : throw new BadHttpRequestException($"Missing required property '{name}'.");
        }

        if (value.ValueKind == JsonValueKind.Null && !nullable)
        {
            throw new BadHttpRequestException($"Property '{name}' cannot be null.");
        }

        var typeInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        var result = value.Deserialize(typeInfo);
        return result is null && !nullable
            ? throw new BadHttpRequestException($"Property '{name}' cannot be null.")
            : result!;
    }

    /// <summary>Reports whether the body supplies a constructor-bound member, so the query is not consulted.</summary>
    /// <typeparam name="TRequest">The request type whose JSON contract names the member.</typeparam>
    /// <typeparam name="TValue">The member's type.</typeparam>
    /// <param name="body">The parsed request body.</param>
    /// <param name="options">The application's frozen Portia JSON options.</param>
    /// <param name="memberIndex">The member's position in the request's primary constructor.</param>
    /// <param name="fallbackName">The wire name to use when JSON metadata is unavailable.</param>
    /// <returns><see langword="true" /> when the body object contains the member, including as JSON null.</returns>
    public static bool HasBodyMember<TRequest, TValue>(JsonElement body, JsonSerializerOptions options,
        int memberIndex, string fallbackName)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw new BadHttpRequestException("Expected a JSON object.");
        var binding = BindingCaches.GetValue(options, static current => new OptionsBindingCache(current))
            .Get(typeof(TRequest), typeof(TValue), memberIndex, fallbackName);
        return TryGetBodyProperty(body, binding.Options, binding.Name, out _);
    }

    static bool TryGetBodyProperty(JsonElement body, JsonSerializerOptions options, string name, out JsonElement value)
    {
        if (body.TryGetProperty(name, out value))
            return true;
        if (!options.PropertyNameCaseInsensitive)
            return false;

        // First match in document order, the same rule TryGetProperty already applies to an
        // exact match — and stop there rather than scanning the rest of the object.
        foreach (var property in body.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    readonly record struct BindingKey(Type RequestType, Type ValueType, int MemberIndex, string FallbackName);

    sealed record BindingMetadata(string Name, JsonSerializerOptions Options);

    sealed class OptionsBindingCache(JsonSerializerOptions options)
    {
        readonly ConcurrentDictionary<BindingKey, Lazy<BindingMetadata>> _bindings = new();

        internal BindingMetadata Get(Type requestType, Type valueType, int memberIndex, string fallbackName) =>
            _bindings.GetOrAdd(new BindingKey(requestType, valueType, memberIndex, fallbackName),
                static (key, state) => new Lazy<BindingMetadata>(() => Resolve(state, key),
                    LazyThreadSafetyMode.ExecutionAndPublication), options).Value;

        static BindingMetadata Resolve(JsonSerializerOptions options, BindingKey key)
        {
            JsonPropertyInfo? property = null;
            try
            {
                var properties = options.GetTypeInfo(key.RequestType).Properties;
                property = properties.FirstOrDefault(candidate => candidate.Name == key.FallbackName)
                           ?? properties.ElementAtOrDefault(key.MemberIndex);
            }
            catch (NotSupportedException)
            {
                // Generated bindings can read scalar members without constructing the request.
                // A transported request-level contract is still validated separately.
            }

            if (property?.CustomConverter is null && property?.NumberHandling is null)
                return new BindingMetadata(property?.Name ?? key.FallbackName, options);

            var derived = new JsonSerializerOptions(options);
            if (property.CustomConverter is not null)
                derived.Converters.Insert(0, property.CustomConverter);
            if (property.NumberHandling is { } numberHandling)
                derived.NumberHandling = numberHandling;
            return new BindingMetadata(property.Name, derived);
        }
    }
}
