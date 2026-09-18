using System.Text.Json;

namespace Cntryl.Portia.Testing;

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
