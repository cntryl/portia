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

    /// <summary>Lists visible fixed resources.</summary>
    public async Task<IReadOnlyList<McpResourceSnapshot>> ListResourcesAsync(CancellationToken cancellationToken = default)
    {
        var resources = await _client.ListResourcesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return resources.Select(resource => new McpResourceSnapshot(resource.Uri, resource.Name,
            resource.MimeType, false)).ToArray();
    }

    /// <summary>Lists visible resource URI templates.</summary>
    public async Task<IReadOnlyList<McpResourceSnapshot>> ListResourceTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        var templates = await _client.ListResourceTemplatesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return templates.Select(template => new McpResourceSnapshot(template.UriTemplate, template.Name,
            template.MimeType, true)).ToArray();
    }

    /// <summary>Reads a resource through MCP.</summary>
    public async Task<IReadOnlyList<McpResourceContentSnapshot>> ReadResourceAsync(string uri,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        var result = await _client.ReadResourceAsync(uri, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Contents.Select(content => content switch
        {
            TextResourceContents text => new McpResourceContentSnapshot(text.Uri, text.MimeType, text.Text, null),
            BlobResourceContents blob => new McpResourceContentSnapshot(blob.Uri, blob.MimeType, null,
                blob.DecodedData.ToArray()),
            _ => throw new InvalidOperationException("Unsupported resource content type.")
        }).ToArray();
    }

    /// <summary>Lists visible prompts.</summary>
    public async Task<IReadOnlyList<McpPromptSnapshot>> ListPromptsAsync(CancellationToken cancellationToken = default)
    {
        var prompts = await _client.ListPromptsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return prompts.Select(prompt => new McpPromptSnapshot(prompt.Name,
            prompt.ProtocolPrompt.Arguments?.ToDictionary(argument => argument.Name, argument => argument.Required == true,
                StringComparer.Ordinal) ?? new Dictionary<string, bool>(StringComparer.Ordinal))).ToArray();
    }

    /// <summary>Gets rendered prompt messages.</summary>
    public async Task<IReadOnlyList<McpPromptMessageSnapshot>> GetPromptAsync(string name,
        IReadOnlyDictionary<string, object?>? arguments = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var result = await _client.GetPromptAsync(name, arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.Messages.Select(message => new McpPromptMessageSnapshot(message.Role.ToString(),
            message.Content is TextContentBlock text ? text.Text : string.Empty)).ToArray();
    }

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
