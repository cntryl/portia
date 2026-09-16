using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cntryl.Portia;

/// <summary>Binding primitives used by generated Portia HTTP endpoints.</summary>
public static class PortiaHttpBinding
{
    // Content-Length is the caller's claim, not a measurement. Sizing the buffer from it lets one
    // small request reserve the whole configured maximum, so the hint is capped: an ordinary body
    // still lands in a single allocation, and a dishonest header cannot reserve more than this.
    const int MaximumInitialBodyBytes = 64 * 1024;
    const int BodyChunkBytes = 16 * 1024;
    static readonly object BodyLimitAppliedKey = new();
    static readonly ConditionalWeakTable<JsonSerializerOptions, OptionsBindingCache> BindingCaches = [];

    static readonly ReadOnlyMemory<byte> EmptyObjectUtf8 = "{}"u8.ToArray();

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

    /// <summary>Applies Portia's body limit to a declared custom request body.</summary>
    /// <param name="context">The current HTTP request.</param>
    public static void EnsureBodyWithinLimit(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var maximum = MaximumBodyBytes(context);
        if (context.Request.ContentLength > maximum)
            throw new HttpPayloadTooLargeException();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature &&
            (feature.MaxRequestBodySize is null || feature.MaxRequestBodySize > maximum))
            feature.MaxRequestBodySize = maximum;
        if (context.Items.ContainsKey(BodyLimitAppliedKey))
            return;
        context.Request.Body = new LimitedRequestBodyStream(context.Request.Body, maximum);
        context.Items.Add(BodyLimitAppliedKey, null);
    }

    /// <summary>Reports whether the request carries an HTML form body.</summary>
    /// <param name="context">The current HTTP request.</param>
    /// <returns><see langword="true" /> for <c>application/x-www-form-urlencoded</c> or <c>multipart/form-data</c>.</returns>
    public static bool HasFormBody(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.HasFormContentType;
    }

    /// <summary>Reads a bounded HTML form body, validating antiforgery when the application registers it.</summary>
    /// <param name="context">The current HTTP request.</param>
    /// <param name="ct">A token that can cancel the read.</param>
    /// <returns>The parsed form.</returns>
    /// <exception cref="HttpPayloadTooLargeException">The body exceeds <c>PortiaHttpOptions.MaxJsonBodyBytes</c>.</exception>
    /// <exception cref="AntiforgeryValidationException">The antiforgery token is missing or invalid.</exception>
    public static async ValueTask<IFormCollection> ReadFormBodyAsync(HttpContext context, CancellationToken ct)
    {
        EnsureBodyWithinLimit(context);
        try
        {
            await ValidateAntiforgeryAsync(context).ConfigureAwait(false);
            return await context.Request.ReadFormAsync(ct).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException ex) when (HasBodyLimitCause(ex))
        {
            throw new HttpPayloadTooLargeException();
        }
        catch (BadHttpRequestException ex) when (IsServerBodyLimit(ex))
        {
            throw new HttpPayloadTooLargeException();
        }
        catch (InvalidDataException ex)
        {
            throw new BadHttpRequestException("Malformed form body.", ex);
        }
    }

    /// <summary>Reads one form field value.</summary>
    /// <param name="form">The parsed form.</param>
    /// <param name="name">The field name.</param>
    /// <param name="emptyIsMissing">Whether an empty value is treated as an absent field.</param>
    /// <returns>The value, or <see langword="null" /> when the field is absent.</returns>
    /// <exception cref="BadHttpRequestException">The field was submitted more than once.</exception>
    public static string? ReadForm(IFormCollection form, string name, bool emptyIsMissing)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!form.TryGetValue(name, out var values) || values.Count == 0)
            return null;
        if (values.Count != 1)
            throw new BadHttpRequestException($"Expected one value for '{name}'.");
        return emptyIsMissing && string.IsNullOrEmpty(values[0]) ? null : values[0];
    }

    /// <summary>
    ///     Reads a form Boolean field. ASP.NET's checkbox helpers post the checkbox value followed by a
    ///     hidden <c>false</c> field of the same name, so repeated values bind <c>true</c> when any is set.
    /// </summary>
    /// <param name="form">The parsed form.</param>
    /// <param name="name">The field name.</param>
    /// <returns><c>true</c>, <c>false</c>, the first unparseable value, or <see langword="null" /> when absent or blank.</returns>
    public static string? ReadFormBoolean(IFormCollection form, string name)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!form.TryGetValue(name, out var values))
            return null;
        var isSet = false;
        var found = false;
        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value))
                continue;
            if (!TryParseFormBoolean(value, out var parsed))
                return value;
            found = true;
            isSet |= parsed;
        }

        return !found ? null : isSet ? bool.TrueString : bool.FalseString;
    }

    /// <summary>Parses a form Boolean, accepting the <c>on</c> value a checked HTML checkbox submits.</summary>
    /// <param name="value">The submitted field value.</param>
    /// <param name="result">The parsed value.</param>
    /// <returns><see langword="true" /> when the value is <c>on</c>, <c>true</c>, or <c>false</c>.</returns>
    public static bool TryParseFormBoolean(string? value, out bool result)
    {
        if (string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        return bool.TryParse(value, out result);
    }

    // A form post can be forged cross-site where a JSON post cannot, so a registered antiforgery
    // service is honoured; endpoints opt out with DisableAntiforgery().
    static async ValueTask ValidateAntiforgeryAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<IAntiforgeryMetadata>() is { RequiresValidation: false })
            return;
        if (context.Features.Get<IAntiforgeryValidationFeature>() is { } feature)
        {
            if (!feature.IsValid)
                throw new AntiforgeryValidationException("Invalid antiforgery token.", feature.Error);
            return;
        }

        if (context.RequestServices.GetService<IAntiforgery>() is { } antiforgery)
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
    }

    /// <summary>Reads one bounded JSON object request body.</summary>
    /// <param name="context">The current HTTP request.</param>
    /// <param name="bodyRequired">Whether an empty body is rejected rather than read as <c>{}</c>.</param>
    /// <param name="ct">A token that can cancel the read.</param>
    /// <returns>The parsed body, or an empty object when the body was empty and optional.</returns>
    /// <exception cref="HttpPayloadTooLargeException">The body exceeds <c>PortiaHttpOptions.MaxJsonBodyBytes</c>.</exception>
    public static async ValueTask<JsonDocument> ReadJsonBodyAsync(HttpContext context, bool bodyRequired,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var maximum = MaximumBodyBytes(context);

        if (context.Request.ContentLength > 0 && context.Request.ContentLength > maximum)
            throw new HttpPayloadTooLargeException();

        var hint = Math.Min(Math.Min(context.Request.ContentLength ?? 0, maximum), MaximumInitialBodyBytes);
        await using var buffer = new MemoryStream((int)hint);
        var chunk = ArrayPool<byte>.Shared.Rent(BodyChunkBytes);
        try
        {
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
        }
        catch (BadHttpRequestException ex) when (IsServerBodyLimit(ex))
        {
            throw new HttpPayloadTooLargeException();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        if (buffer.Length == 0)
        {
            return bodyRequired
                ? throw new BadHttpRequestException("Missing required request body.")
                : JsonDocument.Parse(EmptyObjectUtf8);
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

    // The server (Kestrel's MaxRequestBodySize) can reject an oversized body before Portia counts it;
    // that is the same limit breach, so it surfaces as Portia's 413 rather than a malformed request.
    static bool IsServerBodyLimit(BadHttpRequestException exception) =>
        exception is not HttpPayloadTooLargeException &&
        exception.StatusCode == StatusCodes.Status413PayloadTooLarge;

    static bool HasBodyLimitCause(Exception exception)
    {
        for (var cause = exception.InnerException; cause is not null; cause = cause.InnerException)
        {
            if (cause is HttpPayloadTooLargeException ||
                cause is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
                return true;
        }

        return false;
    }

    static long MaximumBodyBytes(HttpContext context)
    {
        var maximum = context.RequestServices.GetService<IOptions<PortiaHttpOptions>>()?.Value.MaxJsonBodyBytes
                      ?? PortiaHttpOptions.DefaultMaxJsonBodyBytes;
        return maximum > 0
            ? maximum
            : throw new InvalidOperationException($"{nameof(PortiaHttpOptions.MaxJsonBodyBytes)} must be positive.");
    }

    sealed class LimitedRequestBodyStream(Stream inner, long maximum) : Stream
    {
        long _read;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override int ReadByte()
        {
            var value = inner.ReadByte();
            if (value >= 0)
                _ = Count(1);
            return value;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();


        int Count(int read)
        {
            _read += read;
            return _read <= maximum ? read : throw new HttpPayloadTooLargeException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
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

    /// <summary>Reads exactly one nonempty Bearer credential, or null when authorization is absent.</summary>
    /// <param name="context">The current HTTP request.</param>
    /// <returns>The raw credential, or <see langword="null" /> when no Authorization header was sent.</returns>
    /// <exception cref="Microsoft.AspNetCore.Http.BadHttpRequestException">
    ///     The header is present but does not carry exactly one nonempty Bearer credential.
    /// </exception>
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

    /// <summary>Rejects an authenticated identity that cannot be replayed by a durable worker.</summary>
    public static string? ReadPortableBearerCredential(HttpContext context)
    {
        var credential = ReadBearerCredential(context);
        if (credential is null && context.User.Identities.Any(identity => identity.IsAuthenticated))
        {
            throw new BadHttpRequestException(
                "Asynchronous delivery of an authenticated request requires a Bearer credential.");
        }

        return credential;
    }

    /// <summary>Creates the asynchronous acceptance receipt.</summary>
    /// <param name="context">The current HTTP request, whose response gains <c>Preference-Applied</c>.</param>
    /// <param name="requestId">The logical identity the caller can track the enqueued request by.</param>
    /// <returns>A 202 Accepted result carrying the request identity.</returns>
    public static IResult Accepted(HttpContext context, Uuid requestId)
    {
        context.Response.Headers["Preference-Applied"] = "respond-async";
        return new AcceptedReceiptResult(requestId,
            GetJsonOptions(context).PropertyNamingPolicy?.ConvertName("RequestId") ?? "RequestId");
    }

    /// <summary>Writes the stable Portia problem contract.</summary>
    /// <param name="statusCode">The HTTP status to respond with.</param>
    /// <param name="message">The non-sensitive detail reported to the caller.</param>
    /// <returns>A problem-details result.</returns>
    public static IResult Problem(int statusCode, string message) => new ProblemResult(statusCode, message);

    /// <summary>Logs an unexpected HTTP failure and returns a non-sensitive response.</summary>
    /// <param name="context">The current HTTP request.</param>
    /// <param name="exception">The unexpected failure, recorded through Portia's telemetry contract.</param>
    /// <returns>A 500 problem-details result that discloses nothing about the failure.</returns>
    public static IResult Unexpected(HttpContext context, Exception exception)
    {
        PortiaTelemetry.RecordRunnerFault("Http", RunnerFaultStage.Execution, exception,
            context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Cntryl.Portia.Http"));
        return Problem(StatusCodes.Status500InternalServerError, "An unexpected error occurred.");
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
