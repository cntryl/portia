using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cntryl.Portia;

/// <summary>Binding primitives used by generated Portia HTTP endpoints.</summary>
public static class PortiaHttpBinding
{
    static readonly JsonDocument EmptyObject = JsonDocument.Parse("{}");

    /// <summary>Creates execution context from authenticated HTTP state and concrete endpoint facts.</summary>
    public static RequestDispatchContext CreateDispatchContext(HttpContext context)
        => new(context.User, new HttpInvocation(context.Request.Method,
            context.Request.PathBase.Add(context.Request.Path).Value ?? "/",
            (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
            context.TraceIdentifier), timeProvider: context.RequestServices.GetService<TimeProvider>());

    /// <summary>Gets the application's frozen Portia JSON options.</summary>
    public static JsonSerializerOptions GetJsonOptions(HttpContext context)
        => context.RequestServices.GetRequiredService<JsonSerializerOptions>();

    /// <summary>Reads one bounded JSON object request body.</summary>
    public static async ValueTask<JsonDocument> ReadJsonBodyAsync(HttpContext context, bool bodyRequired,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var maximum = context.RequestServices.GetService<IOptions<PortiaHttpOptions>>()?.Value.MaxJsonBodyBytes
                      ?? PortiaHttpOptions.DefaultMaxJsonBodyBytes;
        if (maximum <= 0)
        {
            throw new InvalidOperationException($"{nameof(PortiaHttpOptions.MaxJsonBodyBytes)} must be positive.");
        }

        if (context.Request.ContentLength > 0 && context.Request.ContentLength > maximum)
        {
            throw new HttpPayloadTooLargeException();
        }

        await using var buffer = new MemoryStream((int)Math.Min(context.Request.ContentLength ?? 0, maximum));
        var chunk = new byte[81920];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximum)
            {
                throw new HttpPayloadTooLargeException();
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        if (buffer.Length == 0)
        {
            return bodyRequired
                ? throw new BadHttpRequestException("Missing required request body.")
                : JsonDocument.Parse(EmptyObject.RootElement.GetRawText());
        }

        buffer.Position = 0;
        var json = GetJsonOptions(context);
        return await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions
        {
            AllowTrailingCommas = json.AllowTrailingCommas,
            CommentHandling = json.ReadCommentHandling,
            MaxDepth = json.MaxDepth
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Returns whether an exact RFC preference token requests asynchronous handling.</summary>
    public static bool PrefersRespondAsync(HttpContext context) => context.Request.Headers["Prefer"]
        .SelectMany(value => (value ?? string.Empty).Split(','))
        .Select(value => value.Trim().Split(';', 2)[0].Trim())
        .Any(value => string.Equals(value, "respond-async", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads exactly one nonempty Bearer credential, or null when authorization is absent.</summary>
    public static string? ReadBearerCredential(HttpContext context)
    {
        var values = context.Request.Headers.Authorization;
        if (values.Count == 0)
        {
            return null;
        }

        if (values.Count != 1)
        {
            throw new BadHttpRequestException("Authorization must contain one Bearer credential.");
        }

        var value = values[0];
        if (value is null || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new BadHttpRequestException("Authorization must contain one Bearer credential.");
        }

        var credential = value.AsSpan(7).Trim();
        return credential.IsEmpty || credential.Contains(' ')
            ? throw new BadHttpRequestException("Authorization must contain one Bearer credential.")
            : credential.ToString();
    }

    /// <summary>Creates the asynchronous acceptance receipt.</summary>
    public static IResult Accepted(HttpContext context, Uuid requestId)
    {
        context.Response.Headers["Preference-Applied"] = "respond-async";
        return new AcceptedReceiptResult(requestId,
            GetJsonOptions(context).PropertyNamingPolicy?.ConvertName("RequestId") ?? "RequestId");
    }

    /// <summary>Writes the stable Portia problem contract.</summary>
    public static IResult Problem(int statusCode, string message) => new ProblemResult(statusCode, message);

    /// <summary>Logs an unexpected HTTP failure and returns a non-sensitive response.</summary>
    public static IResult Unexpected(HttpContext context, Exception exception)
    {
        PortiaTelemetry.RecordRunnerFault("Http", RunnerFaultStage.Execution, exception,
            context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Cntryl.Portia.Http"));
        return Problem(StatusCodes.Status500InternalServerError, "An unexpected error occurred.");
    }

    /// <summary>Resolves contextual queue route values configured on the selected endpoint.</summary>
    public static RequestRouteValues ResolveRouteValues(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<PortiaHttpRouteValues>();
        return metadata is null
            ? RequestRouteValues.None
            : metadata.Resolve(context) ??
              throw new InvalidOperationException("The endpoint route-values resolver returned null.");
    }

    /// <summary>Reads one query value, distinguishing omission from an empty string.</summary>
    public static string? ReadQuery(HttpContext context, string name)
    {
        return !context.Request.Query.TryGetValue(name, out var values)
            ? null
            : values.Count == 1
                ? values[0]
                : throw new BadHttpRequestException($"Expected one value for '{name}'.");
    }

    /// <summary>Reads a constructor-bound property using the application's request JSON metadata.</summary>
    public static TValue ReadBody<TRequest, TValue>(JsonElement body, JsonSerializerOptions options, int memberIndex,
        string memberName,
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
            {
                options.Converters.Insert(0, property.CustomConverter);
            }

            if (property.NumberHandling is { } numberHandling)
            {
                options.NumberHandling = numberHandling;
            }
        }

        return ReadBody(body, options, property?.Name ?? fallbackName, nullable, hasDefault, defaultValue);
    }

    /// <summary>Reads a body property with the configured naming, converters and null contract.</summary>
    public static T ReadBody<T>(JsonElement body, JsonSerializerOptions options, string name, bool nullable,
        bool hasDefault, T defaultValue)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new BadHttpRequestException("Expected a JSON object.");
        }

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

    sealed class AcceptedReceiptResult(Uuid requestId, string propertyName) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            context.Response.ContentType = "application/json; charset=utf-8";
            await using var writer = new Utf8JsonWriter(context.Response.Body);
            writer.WriteStartObject();
            writer.WriteString(propertyName, requestId.ToString());
            writer.WriteEndObject();
            await writer.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        }
    }

    sealed class ProblemResult(int statusCode, string message) : IResult
    {
        public Task ExecuteAsync(HttpContext context) => PortiaProblemDetails.WriteAsync(context, statusCode, message);
    }
}
