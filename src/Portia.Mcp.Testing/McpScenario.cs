using System.Runtime.CompilerServices;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Cntryl.Portia.Testing;

/// <summary>An owned official MCP client connected through a caller-owned HTTP client.</summary>
public sealed class McpScenario : IAsyncDisposable
{
    readonly McpClient _client;

    McpScenario(McpClient client) => _client = client;

    /// <summary>Connects to a Streamable HTTP MCP endpoint without taking ownership of the HTTP client.</summary>
    public static async ValueTask<McpScenario> ConnectAsync(HttpClient httpClient, Uri endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpoint);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp
        }, httpClient, loggerFactory: null, ownsHttpClient: false);
        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new McpScenario(client);
    }

    /// <summary>Lists the complete tool catalog and returns awaitable expectations.</summary>
    public McpToolListExpectations ListTools(CancellationToken cancellationToken = default) =>
        new(ListToolsCoreAsync(cancellationToken));

    /// <summary>Invokes a tool and returns awaitable expectations.</summary>
    public McpCallExpectations When(string toolName, IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return new McpCallExpectations(CallCoreAsync(toolName, arguments, cancellationToken));
    }

    async Task<IReadOnlyList<McpToolSnapshot>> ListToolsCoreAsync(CancellationToken cancellationToken)
    {
        var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return tools.Select(tool => McpToolSnapshot.From(tool.ProtocolTool)).ToArray();
    }

    async Task<McpCallSnapshot> CallCoreAsync(string toolName, IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        var result = await _client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var text = result.Content.OfType<TextContentBlock>().Select(block => block.Text).ToArray();
        var structured = result.StructuredContent?.Clone();
        return new McpCallSnapshot(result.IsError == true, text, structured);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

/// <summary>Awaitable assertions over a tool catalog.</summary>
public sealed class McpToolListExpectations
{
    readonly Task<IReadOnlyList<McpToolSnapshot>> _task;

    internal McpToolListExpectations(Task<IReadOnlyList<McpToolSnapshot>> task) => _task = task;

    /// <summary>Requires the catalog to contain exactly the supplied names, ignoring order.</summary>
    public McpToolListExpectations ExpectExactly(params string[] toolNames)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        return new McpToolListExpectations(VerifyAsync(toolNames));
    }

    async Task<IReadOnlyList<McpToolSnapshot>> VerifyAsync(string[] expected)
    {
        var snapshots = await _task.ConfigureAwait(false);
        var actual = snapshots.Select(tool => tool.Name).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var wanted = expected.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(wanted, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected MCP tools [{string.Join(", ", wanted)}], but found [{string.Join(", ", actual)}].");
        }

        return snapshots;
    }

    /// <summary>Returns an awaiter for the immutable catalog snapshot.</summary>
    public TaskAwaiter<IReadOnlyList<McpToolSnapshot>> GetAwaiter() => _task.GetAwaiter();
}

/// <summary>Awaitable assertions over one tool call.</summary>
public sealed class McpCallExpectations
{
    readonly Task<McpCallSnapshot> _task;

    internal McpCallExpectations(Task<McpCallSnapshot> task) => _task = task;

    /// <summary>Requires a successful tool result.</summary>
    public McpCallExpectations ExpectSuccess() => new(VerifyAsync(expectFailure: false, kind: null));

    /// <summary>Requires a failed tool result and, when supplied, its structured failure kind.</summary>
    public McpCallExpectations ExpectFailure(string? kind = null) => new(VerifyAsync(expectFailure: true, kind));

    async Task<McpCallSnapshot> VerifyAsync(bool expectFailure, string? kind)
    {
        var snapshot = await _task.ConfigureAwait(false);
        if (snapshot.IsError != expectFailure)
        {
            throw new InvalidOperationException(expectFailure
                ? "Expected the MCP tool call to fail, but it succeeded."
                : "Expected the MCP tool call to succeed, but it failed.");
        }

        if (kind is not null && (!snapshot.StructuredJson.HasValue
                                 || !snapshot.StructuredJson.Value.TryGetProperty("kind", out var actual)
                                 || !string.Equals(actual.GetString(), kind, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Expected MCP failure kind '{kind}'.");
        }

        return snapshot;
    }

    /// <summary>Returns an awaiter for the immutable call snapshot.</summary>
    public TaskAwaiter<McpCallSnapshot> GetAwaiter() => _task.GetAwaiter();
}

/// <summary>An immutable copy of one advertised MCP tool.</summary>
public sealed class McpToolSnapshot
{
    McpToolSnapshot(string name, string? title, string? description, JsonElement inputSchema,
        JsonElement? outputSchema, bool? readOnly, bool? destructive, bool? idempotent, bool? openWorld,
        JsonElement? metadata)
    {
        Name = name;
        Title = title;
        Description = description;
        InputSchema = inputSchema;
        OutputSchema = outputSchema;
        ReadOnly = readOnly;
        Destructive = destructive;
        Idempotent = idempotent;
        OpenWorld = openWorld;
        Metadata = metadata;
    }

    /// <summary>Gets the protocol tool name.</summary>
    public string Name { get; }
    /// <summary>Gets the optional display title.</summary>
    public string? Title { get; }
    /// <summary>Gets the tool description.</summary>
    public string? Description { get; }
    /// <summary>Gets a detached copy of the input schema.</summary>
    public JsonElement InputSchema { get; }
    /// <summary>Gets a detached copy of the output schema.</summary>
    public JsonElement? OutputSchema { get; }
    /// <summary>Gets the read-only hint.</summary>
    public bool? ReadOnly { get; }
    /// <summary>Gets the destructive hint.</summary>
    public bool? Destructive { get; }
    /// <summary>Gets the idempotent hint.</summary>
    public bool? Idempotent { get; }
    /// <summary>Gets the open-world hint.</summary>
    public bool? OpenWorld { get; }
    /// <summary>Gets a detached copy of protocol metadata.</summary>
    public JsonElement? Metadata { get; }

    internal static McpToolSnapshot From(Tool tool) => new(
        tool.Name,
        tool.Title ?? tool.Annotations?.Title,
        tool.Description,
        tool.InputSchema.Clone(),
        tool.OutputSchema?.Clone(),
        tool.Annotations?.ReadOnlyHint,
        tool.Annotations?.DestructiveHint,
        tool.Annotations?.IdempotentHint,
        tool.Annotations?.OpenWorldHint,
        tool.Meta is null ? null : JsonDocument.Parse(tool.Meta.ToJsonString()).RootElement.Clone());
}

/// <summary>An immutable copy of one MCP tool result.</summary>
public sealed class McpCallSnapshot
{
    internal McpCallSnapshot(bool isError, IReadOnlyList<string> text, JsonElement? structuredJson)
    {
        IsError = isError;
        Text = text;
        StructuredJson = structuredJson;
    }

    /// <summary>Gets whether the server reported a tool failure.</summary>
    public bool IsError { get; }
    /// <summary>Gets detached text content in protocol order.</summary>
    public IReadOnlyList<string> Text { get; }
    /// <summary>Gets a detached copy of structured content.</summary>
    public JsonElement? StructuredJson { get; }
}
