using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Cntryl.Portia.Testing;

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
