using System.Text.Json;

namespace Cntryl.Portia.Testing;

/// <summary>An immutable copy of one MCP tool result.</summary>
public sealed class McpCallSnapshot
{
    internal McpCallSnapshot(bool isError, IReadOnlyList<string> text, JsonElement? structuredJson,
        JsonElement? error)
    {
        IsError = isError;
        Text = text;
        StructuredJson = structuredJson;
        Error = error;
    }

    /// <summary>Gets whether the server reported a tool failure.</summary>
    public bool IsError { get; }

    /// <summary>Gets detached text content in protocol order.</summary>
    public IReadOnlyList<string> Text { get; }

    /// <summary>Gets a detached copy of structured content, which only a successful result carries.</summary>
    public JsonElement? StructuredJson { get; }

    /// <summary>
    ///     Gets a detached copy of Portia's failure details — <c>kind</c>, <c>message</c>, and <c>isTransient</c> —
    ///     from the result metadata, or <see langword="null" /> for a result without them.
    /// </summary>
    public JsonElement? Error { get; }
}
