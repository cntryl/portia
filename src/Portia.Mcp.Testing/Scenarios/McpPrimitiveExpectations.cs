using System.Runtime.CompilerServices;

namespace Cntryl.Portia.Testing;

/// <summary>Awaitable assertions over a resource or resource-template catalog.</summary>
public sealed class McpResourceListExpectations
{
    readonly Task<IReadOnlyList<McpResourceSnapshot>> _task;

    internal McpResourceListExpectations(Task<IReadOnlyList<McpResourceSnapshot>> task) => _task = task;

    /// <summary>Requires the catalog to contain exactly the supplied resource URIs or templates.</summary>
    public McpResourceListExpectations ExpectExactly(params string[] identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        return new McpResourceListExpectations(VerifyAsync(identities));
    }

    /// <summary>Inspects detached resource snapshots.</summary>
    public McpResourceListExpectations Inspect(Action<IReadOnlyList<McpResourceSnapshot>> inspect)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        return new McpResourceListExpectations(InspectAsync(inspect));
    }

    async Task<IReadOnlyList<McpResourceSnapshot>> VerifyAsync(string[] expected)
    {
        var snapshots = await _task.ConfigureAwait(false);
        var actual = snapshots.Select(item => item.Uri).Order(StringComparer.Ordinal).ToArray();
        var wanted = expected.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(wanted, StringComparer.Ordinal))
            throw new InvalidOperationException($"Expected MCP resource identities [{string.Join(", ", wanted)}], but found [{string.Join(", ", actual)}].");
        return snapshots;
    }

    async Task<IReadOnlyList<McpResourceSnapshot>> InspectAsync(Action<IReadOnlyList<McpResourceSnapshot>> inspect)
    {
        var snapshots = await _task.ConfigureAwait(false);
        inspect(snapshots);
        return snapshots;
    }

    /// <summary>Returns an awaiter for the resource snapshots.</summary>
    public TaskAwaiter<IReadOnlyList<McpResourceSnapshot>> GetAwaiter() => _task.GetAwaiter();
}

/// <summary>Awaitable assertions over a prompt catalog.</summary>
public sealed class McpPromptListExpectations
{
    readonly Task<IReadOnlyList<McpPromptSnapshot>> _task;

    internal McpPromptListExpectations(Task<IReadOnlyList<McpPromptSnapshot>> task) => _task = task;

    /// <summary>Requires the catalog to contain exactly the supplied prompt names.</summary>
    public McpPromptListExpectations ExpectExactly(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return new McpPromptListExpectations(VerifyAsync(names));
    }

    /// <summary>Inspects detached prompt snapshots.</summary>
    public McpPromptListExpectations Inspect(Action<IReadOnlyList<McpPromptSnapshot>> inspect)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        return new McpPromptListExpectations(InspectAsync(inspect));
    }

    async Task<IReadOnlyList<McpPromptSnapshot>> VerifyAsync(string[] expected)
    {
        var snapshots = await _task.ConfigureAwait(false);
        var actual = snapshots.Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        var wanted = expected.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(wanted, StringComparer.Ordinal))
            throw new InvalidOperationException($"Expected MCP prompt names [{string.Join(", ", wanted)}], but found [{string.Join(", ", actual)}].");
        return snapshots;
    }

    async Task<IReadOnlyList<McpPromptSnapshot>> InspectAsync(Action<IReadOnlyList<McpPromptSnapshot>> inspect)
    {
        var snapshots = await _task.ConfigureAwait(false);
        inspect(snapshots);
        return snapshots;
    }

    /// <summary>Returns an awaiter for the prompt snapshots.</summary>
    public TaskAwaiter<IReadOnlyList<McpPromptSnapshot>> GetAwaiter() => _task.GetAwaiter();
}

/// <summary>Awaitable assertions over resource content.</summary>
public sealed class McpResourceReadExpectations
{
    readonly Task<IReadOnlyList<McpResourceContentSnapshot>> _task;

    internal McpResourceReadExpectations(Task<IReadOnlyList<McpResourceContentSnapshot>> task) => _task = task;

    /// <summary>Inspects detached resource content.</summary>
    public McpResourceReadExpectations Inspect(Action<IReadOnlyList<McpResourceContentSnapshot>> inspect)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        return new McpResourceReadExpectations(InspectAsync(inspect));
    }

    async Task<IReadOnlyList<McpResourceContentSnapshot>> InspectAsync(Action<IReadOnlyList<McpResourceContentSnapshot>> inspect)
    {
        var snapshots = await _task.ConfigureAwait(false);
        inspect(snapshots);
        return snapshots;
    }

    /// <summary>Returns an awaiter for the resource content snapshots.</summary>
    public TaskAwaiter<IReadOnlyList<McpResourceContentSnapshot>> GetAwaiter() => _task.GetAwaiter();
}

/// <summary>Awaitable assertions over rendered prompt messages.</summary>
public sealed class McpPromptGetExpectations
{
    readonly Task<IReadOnlyList<McpPromptMessageSnapshot>> _task;

    internal McpPromptGetExpectations(Task<IReadOnlyList<McpPromptMessageSnapshot>> task) => _task = task;

    /// <summary>Inspects detached rendered messages.</summary>
    public McpPromptGetExpectations Inspect(Action<IReadOnlyList<McpPromptMessageSnapshot>> inspect)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        return new McpPromptGetExpectations(InspectAsync(inspect));
    }

    async Task<IReadOnlyList<McpPromptMessageSnapshot>> InspectAsync(Action<IReadOnlyList<McpPromptMessageSnapshot>> inspect)
    {
        var snapshots = await _task.ConfigureAwait(false);
        inspect(snapshots);
        return snapshots;
    }

    /// <summary>Returns an awaiter for the rendered prompt messages.</summary>
    public TaskAwaiter<IReadOnlyList<McpPromptMessageSnapshot>> GetAwaiter() => _task.GetAwaiter();
}
