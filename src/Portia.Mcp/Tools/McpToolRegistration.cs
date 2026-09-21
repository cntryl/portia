using System.Buffers;
using System.ComponentModel;
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

            return (TRequest?)arguments.Deserialize(json.GetTypeInfo(typeof(TRequest)))
                   ?? throw new JsonException("The MCP tool input cannot be null.");
        }
        catch (JsonException exception)
        {
            throw new McpBindingException(exception);
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
        var provider = services.GetService<IMcpActorProvider>()
                       ?? throw new McpActorRequiredException(
                           "MCP ingress has no authenticated principal or registered IMcpActorProvider.");
        return await provider.GetActorAsync(ct).ConfigureAwait(false)
               ?? throw new McpActorRequiredException("IMcpActorProvider returned null.");
    }

    /// <summary>Maps an expected Portia failure to an MCP tool failure.</summary>
    protected static CallToolResult Failure(RequestError error) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = error.Message }],
        StructuredContent = Element(new JsonObject
        {
            ["kind"] = error.Kind.ToString(),
            ["message"] = error.Message,
            ["isTransient"] = error.IsTransient
        })
    };

    internal static CallToolResult IngressFailure(string kind, string message, bool transient = false) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
        StructuredContent = Element(new JsonObject
        {
            ["kind"] = kind,
            ["message"] = message,
            ["isTransient"] = transient
        })
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
    internal override async ValueTask<McpToolInvocationResult> InvokeAsync(
        ModelContextProtocol.Server.RequestContext<CallToolRequestParams> call,
        JsonSerializerOptions json, CancellationToken ct)
    {
        var services = call.Services ?? throw new InvalidOperationException("The MCP request has no service scope.");
        var request = Bind<TRequest>(call.Params, json);
        var actor = await ActorAsync(call, ct).ConfigureAwait(false);
        var bus = services.GetRequiredService<IRequestBus>();
        var context = new RequestDispatchContext(actor, Invocation,
            timeProvider: services.GetService<TimeProvider>());
        var result = await bus.DispatchAsync(request, context, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? new McpToolInvocationResult(
                new CallToolResult { IsError = false, Content = [new TextContentBlock { Text = "Succeeded." }] }, null)
            : new McpToolInvocationResult(Failure(result.Error!), result.Error);
    }
}

/// <summary>Generated descriptor for a result-bearing MCP request.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class McpToolRegistration<TRequest, TOut>(string name, string description, McpToolOptions options)
    : McpToolRegistration(typeof(TRequest), typeof(TOut), name, description, options)
    where TRequest : IRequest<TOut>, ICallable
{
    internal override async ValueTask<McpToolInvocationResult> InvokeAsync(
        ModelContextProtocol.Server.RequestContext<CallToolRequestParams> call,
        JsonSerializerOptions json, CancellationToken ct)
    {
        var services = call.Services ?? throw new InvalidOperationException("The MCP request has no service scope.");
        var request = Bind<TRequest>(call.Params, json);
        var actor = await ActorAsync(call, ct).ConfigureAwait(false);
        var bus = services.GetRequiredService<IRequestBus>();
        var context = new RequestDispatchContext(actor, Invocation,
            timeProvider: services.GetService<TimeProvider>());
        var result = await bus.DispatchAsync(request, context, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            return new McpToolInvocationResult(Failure(result.Error!), result.Error);
        var value = StructuredResult(result.Value, (JsonTypeInfo<TOut>)json.GetTypeInfo(typeof(TOut)));
        return new McpToolInvocationResult(new CallToolResult
        {
            IsError = false,
            StructuredContent = value,
            Content = [new TextContentBlock { Text = value.GetRawText() }]
        }, null);
    }
}
