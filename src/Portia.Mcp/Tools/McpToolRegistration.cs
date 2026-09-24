using System.Buffers;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace Cntryl.Portia;

/// <summary>Compile-time descriptor for one explicitly exposed MCP tool.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class McpToolRegistration
{
    static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> BindingOptions = [];
    Tool? _protocolTool;

    internal McpToolRegistration(Type requestType, Type? resultType, string name, string description,
        McpToolOptions options)
    {
        RequestType = requestType;
        ResultType = resultType;
        RequestName = name;
        Name = string.IsNullOrWhiteSpace(options.Name) ? name : options.Name;
        Description = string.IsNullOrWhiteSpace(options.Description) ? description : options.Description;
        if (!IsValidName(Name))
            throw new ArgumentException(
                $"MCP tool name '{Name}' must contain 1 to 128 ASCII letters, digits, dots, underscores, or hyphens.",
                nameof(options));
        if (string.IsNullOrWhiteSpace(Description))
            throw new ArgumentException(
                $"MCP tool '{Name}' requires a non-empty description.", nameof(options));
        Title = options.Title;
        ReadOnly = options.ReadOnlyHint;
        Destructive = options.DestructiveHint;
        Idempotent = options.IdempotentHint;
        OpenWorld = options.OpenWorldHint;
        Invocation = new McpInvocation(Name);
    }

    internal string RequestName { get; }

    internal McpInvocation Invocation { get; }

    /// <summary>Gets the concrete Portia request type.</summary>
    public Type RequestType { get; }

    /// <summary>Gets the successful result type, when present.</summary>
    public Type? ResultType { get; }

    /// <summary>Gets the stable MCP tool name.</summary>
    public string Name { get; }

    /// <summary>Gets the model-facing description.</summary>
    public string Description { get; }

    /// <summary>Gets the optional human-readable title.</summary>
    public string? Title { get; }

    /// <summary>Gets the read-only hint.</summary>
    public bool? ReadOnly { get; }

    /// <summary>Gets the destructive hint.</summary>
    public bool? Destructive { get; }

    /// <summary>Gets the idempotent hint.</summary>
    public bool? Idempotent { get; }

    /// <summary>Gets the open-world hint.</summary>
    public bool? OpenWorld { get; }

    internal Tool CreateProtocolTool(JsonSerializerOptions json)
    {
        if (_protocolTool is not null)
            return _protocolTool;
        var input = json.GetTypeInfo(RequestType).GetJsonSchemaAsNode();
        if (input is JsonObject inputObject)
            inputObject["type"] = "object";
        var result = ResultType is null ? null : json.GetTypeInfo(ResultType).GetJsonSchemaAsNode();
        if (result is not null)
            RewriteResultReferences(result);
        JsonNode? output = result is null
            ? null
            : new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["result"] = result },
                ["required"] = new JsonArray("result")
            };
        var tool = new Tool
        {
            Name = Name,
            Description = Description,
            Title = Title,
            InputSchema = Element(input),
            OutputSchema = output is null ? null : Element(output),
            Annotations = new ToolAnnotations
            {
                Title = Title,
                ReadOnlyHint = ReadOnly,
                DestructiveHint = Destructive,
                IdempotentHint = Idempotent,
                OpenWorldHint = OpenWorld
            }
        };
        return Interlocked.CompareExchange(ref _protocolTool, tool, null) ?? tool;
    }

    internal abstract ValueTask<McpToolInvocationResult> InvokeAsync(
        ModelContextProtocol.Server.RequestContext<CallToolRequestParams> call,
        JsonSerializerOptions json, CancellationToken ct);

    /// <summary>Binds, attributes, and dispatches one MCP call, projecting only a successful result.</summary>
    private protected async ValueTask<McpToolInvocationResult> DispatchAsync<TRequest, TResult>(
        ModelContextProtocol.Server.RequestContext<CallToolRequestParams> call,
        JsonSerializerOptions json,
        Func<IRequestBus, TRequest, RequestDispatchContext, CancellationToken, ValueTask<TResult>> dispatch,
        Func<TResult, RequestError?> failure,
        Func<TResult, JsonSerializerOptions, CallToolResult> success,
        CancellationToken ct)
    {
        var services = call.Services ?? throw new InvalidOperationException("The MCP request has no service scope.");
        var request = Bind<TRequest>(call.Params, json);
        var actor = await ActorAsync(call, ct).ConfigureAwait(false);
        var bus = services.GetRequiredService<IRequestBus>();
        var context = new RequestDispatchContext(actor, Invocation,
            timeProvider: services.GetService<TimeProvider>());
        var result = await dispatch(bus, request, context, ct).ConfigureAwait(false);
        return failure(result) is { } error
            ? new McpToolInvocationResult(Failure(error), error)
            : new McpToolInvocationResult(success(result, json), null);
    }

    /// <summary>Binds one generated request from untrusted MCP arguments.</summary>
    protected static TRequest Bind<TRequest>(CallToolRequestParams call, JsonSerializerOptions json)
    {
        try
        {
            var arguments = new JsonObject();
            if (call.Arguments is not null)
            {
                foreach (var argument in call.Arguments)
                    arguments[argument.Key] = JsonNode.Parse(argument.Value.GetRawText());
            }

            return (TRequest?)arguments.Deserialize(Strict(json).GetTypeInfo(typeof(TRequest)))
                   ?? throw new JsonException("The MCP tool input cannot be null.");
        }
        catch (JsonException exception)
        {
            throw new McpBindingException(exception);
        }
    }

    // Mirrors generated HTTP binding: a constructor member that is neither nullable nor defaulted must
    // be present and non-null.
    static JsonSerializerOptions Strict(JsonSerializerOptions json) =>
        BindingOptions.GetValue(json, static current =>
        {
            var options = new JsonSerializerOptions(current)
            {
                RespectNullableAnnotations = true,
                TypeInfoResolver = current.TypeInfoResolver?.WithAddedModifier(RequireNonNullableMembers)
            };
            options.MakeReadOnly();
            return options;
        });

    static void RequireNonNullableMembers(JsonTypeInfo typeInfo)
    {
        foreach (var property in typeInfo.Properties)
        {
            if (property.AssociatedParameter is { HasDefaultValue: false, IsNullable: false })
                property.IsRequired = true;
        }
    }

    /// <summary>Resolves the authenticated or explicitly provided actor.</summary>
    protected static async ValueTask<ClaimsPrincipal> ActorAsync(
        ModelContextProtocol.Server.RequestContext<CallToolRequestParams> call,
        CancellationToken ct)
    {
        if (call.User is not null)
            return call.User;
        var services = call.Services ?? throw new InvalidOperationException("The MCP request has no service scope.");
        // HTTP authentication owns identity: the SDK omits an unauthenticated caller's principal, so it
        // stays anonymous and the request bus fails closed. The actor provider is a stdio-only policy.
        if (services.GetService<PortiaMcpHttpMarker>() is not null)
            return new ClaimsPrincipal(new ClaimsIdentity());
        var provider = services.GetService<IMcpActorProvider>()
                       ?? throw new McpActorRequiredException(
                           "MCP ingress has no authenticated principal or registered IMcpActorProvider.");
        return await provider.GetActorAsync(ct).ConfigureAwait(false)
               ?? throw new McpActorRequiredException("IMcpActorProvider returned null.");
    }

    /// <summary>The result metadata key that carries Portia's failure kind, message, and transience.</summary>
    internal const string FailureMetadataKey = "portia/error";

    /// <summary>Maps an expected Portia failure to an MCP tool failure.</summary>
    protected static CallToolResult Failure(RequestError error) =>
        IngressFailure(error.Kind.ToString(), error.Message, error.IsTransient);

    // A failure carries no structured content: the protocol requires structured content to conform to the tool's
    // output schema, which describes a successful result. The message is the text a model reads; the kind and
    // transience ride in result metadata, which clients read without validating against that schema.
    internal static CallToolResult IngressFailure(string kind, string message, bool transient = false) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
        Meta = new JsonObject
        {
            [FailureMetadataKey] = new JsonObject
            {
                ["kind"] = kind,
                ["message"] = message,
                ["isTransient"] = transient
            }
        }
    };

    internal static JsonElement StructuredResult<TOut>(TOut value, JsonTypeInfo<TOut> typeInfo)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("result");
            JsonSerializer.Serialize(writer, value, typeInfo);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    static bool IsValidName(string name)
    {
        if (name.Length is < 1 or > 128)
            return false;
        foreach (var character in name)
        {
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
                    or '_' or '-' or '.'))
                return false;
        }

        return true;
    }

    static void RewriteResultReferences(JsonNode node)
    {
        if (node is JsonObject objectNode)
        {
            foreach (var property in objectNode.ToArray())
            {
                if (property.Key == "$ref" && property.Value?.GetValue<string>() is { } reference
                                           && (reference == "#" ||
                                               reference.StartsWith("#/", StringComparison.Ordinal)))
                {
                    objectNode[property.Key] = reference == "#"
                        ? "#/properties/result"
                        : string.Concat("#/properties/result", reference.AsSpan(1));
                }
                else if (property.Value is not null)
                {
                    RewriteResultReferences(property.Value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
                if (item is not null)
                    RewriteResultReferences(item);
        }
    }

    static JsonElement Element(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}

/// <summary>Generated descriptor for a no-result MCP request.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class McpToolRegistration<TRequest>(string name, string description, McpToolOptions options)
    : McpToolRegistration(typeof(TRequest), null, name, description, options)
    where TRequest : IRequest, ICallable
{
    internal override ValueTask<McpToolInvocationResult> InvokeAsync(
        ModelContextProtocol.Server.RequestContext<CallToolRequestParams> call,
        JsonSerializerOptions json, CancellationToken ct) =>
        DispatchAsync<TRequest, Result>(call, json,
            static (bus, request, context, token) => bus.DispatchAsync(request, context, token),
            static result => result.IsSuccess ? null : result.Error!,
            static (_, _) => new CallToolResult
            {
                IsError = false,
                Content = [new TextContentBlock { Text = "Succeeded." }]
            },
            ct);
}

/// <summary>Generated descriptor for a result-bearing MCP request.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class McpToolRegistration<TRequest, TOut>(string name, string description, McpToolOptions options)
    : McpToolRegistration(typeof(TRequest), typeof(TOut), name, description, options)
    where TRequest : IRequest<TOut>, ICallable
{
    internal override ValueTask<McpToolInvocationResult> InvokeAsync(
        ModelContextProtocol.Server.RequestContext<CallToolRequestParams> call,
        JsonSerializerOptions json, CancellationToken ct) =>
        DispatchAsync<TRequest, Result<TOut>>(call, json,
            static (bus, request, context, token) => bus.DispatchAsync(request, context, token),
            static result => result.IsSuccess ? null : result.Error!,
            static (result, options) =>
            {
                var value = StructuredResult(result.Value, (JsonTypeInfo<TOut>)options.GetTypeInfo(typeof(TOut)));
                return new CallToolResult
                {
                    IsError = false,
                    StructuredContent = value,
                    Content = [new TextContentBlock { Text = value.GetRawText() }]
                };
            },
            ct);
}
