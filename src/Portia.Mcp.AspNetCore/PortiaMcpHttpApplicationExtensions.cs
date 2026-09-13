using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace Cntryl.Portia;

/// <summary>Activates Portia MCP tools over ASP.NET Core Streamable HTTP.</summary>
public static class PortiaMcpHttpApplicationExtensions
{
    /// <summary>Registers stateless Streamable HTTP services for the declared Portia MCP tools.</summary>
    /// <param name="application">The shared Portia application.</param>
    /// <param name="configure">Optional HTTP transport configuration applied after Portia's stateless default.</param>
    /// <returns>The same application builder for chaining.</returns>
    public static PortiaBuilder AddMcpHttp(this PortiaBuilder application,
        Action<HttpServerTransportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!application.Services.Any(service => service.ServiceType == typeof(McpToolRegistration)))
            throw new InvalidOperationException(
                "AddMcpHttp requires at least one AddMcpTool<TRequest>() declaration.");
        _ = application.Services.AddMcpServer()
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
                configure?.Invoke(options);
            })
            .AddAuthorizationFilters();
        _ = application.Services.AddSingleton<PortiaMcpHttpMarker>();
        return application;
    }
}

sealed class PortiaMcpHttpMarker;
