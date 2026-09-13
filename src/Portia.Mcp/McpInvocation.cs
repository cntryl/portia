namespace Cntryl.Portia;

/// <summary>Describes a request received through a Model Context Protocol tool call.</summary>
/// <param name="ToolName">The stable MCP tool name.</param>
public sealed record McpInvocation(string ToolName) : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "mcp";

    /// <inheritdoc />
    public override RequestTraceRelationship TraceRelationship => RequestTraceRelationship.Parent;
}
