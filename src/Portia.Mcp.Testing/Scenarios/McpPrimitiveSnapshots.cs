using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Cntryl.Portia.Testing;

/// <summary>A resource visible through MCP discovery.</summary>
public sealed record McpResourceSnapshot(string Uri, string Name, string? MimeType, bool IsTemplate)
{
    /// <summary>Gets the human-readable title, when supplied.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the resource description, when supplied.</summary>
    public string? Description { get; init; }

    /// <summary>Gets detached protocol annotations, when supplied.</summary>
    public JsonElement? Annotations { get; init; }

    /// <summary>Gets detached protocol metadata, when supplied.</summary>
    public JsonElement? Metadata { get; init; }
}

/// <summary>A resource content block returned by MCP.</summary>
public sealed record McpResourceContentSnapshot(string Uri, string? MimeType, string? Text, byte[]? Bytes)
{
    /// <summary>Gets detached protocol metadata, when supplied.</summary>
    public JsonElement? Metadata { get; init; }

    /// <summary>Gets the response cache lifetime, when supplied by the server.</summary>
    public TimeSpan? TimeToLive { get; init; }

    /// <summary>Gets the response cache scope, when supplied by the server.</summary>
    public CacheScope? CacheScope { get; init; }
}

/// <summary>A prompt visible through MCP discovery.</summary>
public sealed record McpPromptSnapshot(string Name, IReadOnlyDictionary<string, bool> Arguments)
{
    /// <summary>Gets the human-readable title, when supplied.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the prompt description, when supplied.</summary>
    public string? Description { get; init; }

    /// <summary>Gets detached protocol metadata, when supplied.</summary>
    public JsonElement? Metadata { get; init; }

    /// <summary>Gets detached argument descriptions by argument name.</summary>
    public IReadOnlyDictionary<string, string?> ArgumentDescriptions { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);
}

/// <summary>A rendered MCP prompt message.</summary>
public sealed record McpPromptMessageSnapshot(string Role, string Text)
{
    /// <summary>Gets detached message metadata, when supplied.</summary>
    public JsonElement? Metadata { get; init; }

    /// <summary>Gets detached text content metadata, when supplied.</summary>
    public JsonElement? ContentMetadata { get; init; }

}
