using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;

namespace Cntryl.Portia;

/// <summary>Activates Portia MCP tools over ASP.NET Core Streamable HTTP.</summary>
public static class PortiaMcpHttpApplicationExtensions
{
    /// <summary>Registers stateless Streamable HTTP with a fresh asynchronous service scope per operation.</summary>
    /// <param name="application">The shared Portia application.</param>
    /// <param name="configure">Optional HTTP transport configuration applied after Portia's stateless default.</param>
    /// <returns>The same application builder for chaining.</returns>
    public static PortiaBuilder AddMcpHttp(this PortiaBuilder application,
        Action<HttpServerTransportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!PortiaMcpApplicationExtensions.HasDeclarations(application.Services))
            throw new InvalidOperationException(
                "AddMcpHttp requires at least one MCP declaration.");
        application.Services.AddAuthorization();
        var server = application.Services.AddMcpServer()
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
                configure?.Invoke(options);
                var configureSession = options.ConfigureSessionOptions;
                options.ConfigureSessionOptions = async (context, serverOptions, token) =>
                {
                    if (configureSession is not null)
                        await configureSession(context, serverOptions, token).ConfigureAwait(false);
                    // The SDK disables operation scopes for stateless HTTP before calling this hook.
                    // Own the invocation lifetime so aborted response streams cannot retain application services.
                    serverOptions.ScopeRequests = true;
                };
            })
            .AddAuthorizationFilters();
        McpPrimitiveHandlers.Attach(server, application.Services);
        McpPrimitiveHandlers.MarkTransportActivated(application.Services, "AddMcpHttp");
        _ = application.Services.AddSingleton<PortiaMcpHttpMarker>();
        PortiaCrossOrigin.AddCorsDecisions(application.Services);
        return application;
    }
}
