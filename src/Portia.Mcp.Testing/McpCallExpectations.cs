using System.Runtime.CompilerServices;

namespace Cntryl.Portia.Testing;

/// <summary>Awaitable assertions over one tool call.</summary>
public sealed class McpCallExpectations
{
    readonly Task<McpCallSnapshot> _task;

    internal McpCallExpectations(Task<McpCallSnapshot> task)
    {
        _task = task;
    }

    /// <summary>Requires a successful tool result.</summary>
    public McpCallExpectations ExpectSuccess() => new(VerifyAsync(false, null));

    /// <summary>Requires a failed tool result and, when supplied, its structured failure kind.</summary>
    public McpCallExpectations ExpectFailure(string? kind = null) => new(VerifyAsync(true, kind));

    async Task<McpCallSnapshot> VerifyAsync(bool expectFailure, string? kind)
    {
        var snapshot = await _task.ConfigureAwait(false);
        if (snapshot.IsError != expectFailure)
        {
            throw new InvalidOperationException(expectFailure
                ? "Expected the MCP tool call to fail, but it succeeded."
                : "Expected the MCP tool call to succeed, but it failed.");
        }

        if (kind is not null && (!snapshot.StructuredJson.HasValue
                                 || !snapshot.StructuredJson.Value.TryGetProperty("kind", out var actual)
                                 || !string.Equals(actual.GetString(), kind, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Expected MCP failure kind '{kind}'.");
        }

        return snapshot;
    }

    /// <summary>Returns an awaiter for the immutable call snapshot.</summary>
    public TaskAwaiter<McpCallSnapshot> GetAwaiter() => _task.GetAwaiter();
}
