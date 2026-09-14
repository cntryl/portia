namespace Cntryl.Portia;

/// <summary>Describes how an explicitly selected Portia request appears to MCP clients.</summary>
public sealed class McpToolOptions
{
    /// <summary>Overrides the request discriminator used as the tool name.</summary>
    internal string? Name { get; private set; }

    /// <summary>Overrides the request XML summary used as the tool description.</summary>
    internal string? Description { get; private set; }

    /// <summary>Gets or sets the optional human-readable title.</summary>
    internal string? Title { get; private set; }

    /// <summary>Gets or sets whether the tool only reads state.</summary>
    internal bool? ReadOnlyHint { get; private set; }

    /// <summary>Gets or sets whether the tool may destructively change state.</summary>
    internal bool? DestructiveHint { get; private set; }

    /// <summary>Gets or sets whether repeating the same call has no additional effect.</summary>
    internal bool? IdempotentHint { get; private set; }

    /// <summary>Gets or sets whether the tool may interact with entities outside a closed domain.</summary>
    internal bool? OpenWorldHint { get; private set; }

    /// <summary>Overrides the stable discriminator-derived tool name.</summary>
    public McpToolOptions Named(string name)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A tool name cannot be empty.", nameof(name))
            : name;
        return this;
    }

    /// <summary>Overrides the request XML summary used as the model-facing description.</summary>
    public McpToolOptions DescribedAs(string description)
    {
        Description = string.IsNullOrWhiteSpace(description)
            ? throw new ArgumentException("A tool description cannot be empty.", nameof(description))
            : description;
        return this;
    }

    /// <summary>Adds a human-readable display title.</summary>
    public McpToolOptions Titled(string title)
    {
        Title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("A tool title cannot be empty.", nameof(title))
            : title;
        return this;
    }

    /// <summary>Marks the tool as reading without changing state.</summary>
    public McpToolOptions ReadOnly()
    {
        ReadOnlyHint = true;
        DestructiveHint = false;
        return this;
    }

    /// <summary>Marks the tool as capable of destructive state changes.</summary>
    public McpToolOptions Destructive()
    {
        ReadOnlyHint = false;
        DestructiveHint = true;
        return this;
    }

    /// <summary>Marks repeated identical calls as having no additional effect.</summary>
    public McpToolOptions Idempotent()
    {
        IdempotentHint = true;
        return this;
    }

    /// <summary>Marks the tool as interacting with entities outside a closed domain.</summary>
    public McpToolOptions OpenWorld()
    {
        OpenWorldHint = true;
        return this;
    }
}
