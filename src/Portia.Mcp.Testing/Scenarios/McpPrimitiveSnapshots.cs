namespace Cntryl.Portia.Testing;

/// <summary>A resource visible through MCP discovery.</summary>
public sealed record McpResourceSnapshot(string Uri, string Name, string? MimeType, bool IsTemplate);

/// <summary>A resource content block returned by MCP.</summary>
public sealed record McpResourceContentSnapshot(string Uri, string? MimeType, string? Text, byte[]? Bytes);

/// <summary>A prompt visible through MCP discovery.</summary>
public sealed record McpPromptSnapshot(string Name, IReadOnlyDictionary<string, bool> Arguments);

/// <summary>A rendered MCP prompt message.</summary>
public sealed record McpPromptMessageSnapshot(string Role, string Text);
