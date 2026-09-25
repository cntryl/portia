namespace Cntryl.Portia;

/// <summary>Identifies an MCP resource read in Portia telemetry.</summary>
public sealed record McpResourceInvocation(string UriTemplate) : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "mcp-resource";
    /// <inheritdoc />
    public override RequestTraceRelationship TraceRelationship => RequestTraceRelationship.Parent;
}

/// <summary>Identifies an MCP prompt get in Portia telemetry.</summary>
public sealed record McpPromptInvocation(string PromptName) : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "mcp-prompt";
    /// <inheritdoc />
    public override RequestTraceRelationship TraceRelationship => RequestTraceRelationship.Parent;
}
