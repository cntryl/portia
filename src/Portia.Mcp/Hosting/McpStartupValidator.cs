using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

sealed class McpStartupValidator(
    [FromKeyedServices(PortiaServiceKeys.Json)] JsonSerializerOptions json,
    IEnumerable<McpToolRegistration> tools,
    IEnumerable<McpResourceEntry> resources,
    IEnumerable<McpPromptEntry> prompts,
    IEnumerable<RequestHandlerRegistration> handlers) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var tool in tools)
            _ = tool.CreateProtocolTool(json);
        var handled = handlers.Select(handler => handler.RequestType).ToHashSet();
        var missing = tools.Select(tool => (RequestType: tool.RequestType, Name: tool.Name))
            .Concat(resources.Select(resource => (RequestType: resource.RequestType, Name: resource.Template)))
            .Concat(prompts.Select(prompt => (RequestType: prompt.RequestType, Name: prompt.Name)))
            .Where(entry => !handled.Contains(entry.RequestType))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"MCP declarations require registered request handlers: {string.Join(", ", missing.Select(entry => $"'{entry.Name}' ({entry.RequestType})"))}.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
