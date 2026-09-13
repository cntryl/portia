using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Maps Portia MCP tools through ASP.NET Core Streamable HTTP.</summary>
public static class PortiaMcpEndpointRouteBuilderExtensions
{
    /// <summary>Maps the MCP endpoint for tools declared by the shared Portia application.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The Streamable HTTP route prefix.</param>
    /// <returns>A builder for applying standard ASP.NET Core endpoint conventions.</returns>
    public static IEndpointConventionBuilder MapPortiaMcp(this IEndpointRouteBuilder endpoints,
        string pattern = "/mcp")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        if (!endpoints.ServiceProvider.GetServices<McpToolRegistration>().Any())
            throw new InvalidOperationException(
                "MapPortiaMcp requires at least one AddMcpTool<TRequest>() declaration.");
        return endpoints.MapMcp(pattern);
    }
}
