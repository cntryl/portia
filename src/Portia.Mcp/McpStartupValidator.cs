using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

sealed class McpStartupValidator(
    JsonSerializerOptions json,
    IEnumerable<McpToolRegistration> tools,
    IEnumerable<RequestHandlerRegistration> handlers) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var tool in tools)
            _ = tool.CreateProtocolTool(json);
        var handled = handlers.Select(handler => handler.RequestType).ToHashSet();
        var missing = tools.Where(tool => !handled.Contains(tool.RequestType))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"MCP tools require registered request handlers: {string.Join(", ", missing.Select(tool => $"'{tool.Name}' ({tool.RequestType})"))}.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
