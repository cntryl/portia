using System.Runtime.CompilerServices;
using System.Text.Json;

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

    /// <summary>Requires a failed tool result and, when supplied, its Portia failure kind.</summary>
    public McpCallExpectations ExpectFailure(string? kind = null) => new(VerifyAsync(true, kind));

    async Task<McpCallSnapshot> VerifyAsync(bool expectFailure, string? kind)
    {
        var snapshot = await _task.ConfigureAwait(false);
        if (snapshot.IsError != expectFailure)
        {
            throw new InvalidOperationException(expectFailure
                ? $"Expected the MCP tool call to fail, but it succeeded.{Describe(snapshot)}"
                : $"Expected the MCP tool call to succeed, but it failed.{Describe(snapshot)}");
        }

        if (kind is not null && !string.Equals(KindOf(snapshot), kind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected MCP failure kind '{kind}', but the failure {(KindOf(snapshot) is { } actual ? $"had kind '{actual}'" : "carried no string kind")}.{Describe(snapshot)}");
        }

        return snapshot;
    }

    static string? KindOf(McpCallSnapshot snapshot) =>
        snapshot.Error is { ValueKind: JsonValueKind.Object } error
        && error.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
            ? kind.GetString()
            : null;

    static string Describe(McpCallSnapshot snapshot) =>
        snapshot.Text.Count == 0 ? string.Empty : $" Tool text: {string.Join(" ", snapshot.Text)}";

    /// <summary>Returns an awaiter for the immutable call snapshot.</summary>
    public TaskAwaiter<McpCallSnapshot> GetAwaiter() => _task.GetAwaiter();
}
