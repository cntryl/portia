using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Cntryl.Portia.Testing;

/// <summary>An owned official MCP client connected through a caller-owned HTTP client.</summary>
public sealed class McpScenario : IAsyncDisposable
{
    readonly McpClient _client;

    McpScenario(McpClient client)
    {
        _client = client;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _client.DisposeAsync();

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
        }, httpClient, null, false);
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
        JsonElement? error = null;
        if (result.Meta?[McpFailureMetadata.Key] is { } failure)
        {
            using var document = JsonDocument.Parse(failure.ToJsonString());
            error = document.RootElement.Clone();
        }

        return new McpCallSnapshot(result.IsError == true, text, structured, error);
    }
}
