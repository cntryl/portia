using System.Runtime.CompilerServices;

namespace Cntryl.Portia.Testing;

/// <summary>Awaitable assertions over a tool catalog.</summary>
public sealed class McpToolListExpectations
{
    readonly Task<IReadOnlyList<McpToolSnapshot>> _task;

    internal McpToolListExpectations(Task<IReadOnlyList<McpToolSnapshot>> task)
    {
        _task = task;
    }

    /// <summary>Requires the catalog to contain exactly the supplied names, ignoring order.</summary>
    public McpToolListExpectations ExpectExactly(params string[] toolNames)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        return new McpToolListExpectations(VerifyAsync(toolNames));
    }

    async Task<IReadOnlyList<McpToolSnapshot>> VerifyAsync(string[] expected)
    {
        var snapshots = await _task.ConfigureAwait(false);
        var actual = snapshots.Select(tool => tool.Name).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var wanted = expected.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(wanted, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected MCP tools [{string.Join(", ", wanted)}], but found [{string.Join(", ", actual)}].");
        }

        return snapshots;
    }

    /// <summary>Returns an awaiter for the immutable catalog snapshot.</summary>
    public TaskAwaiter<IReadOnlyList<McpToolSnapshot>> GetAwaiter() => _task.GetAwaiter();
}
