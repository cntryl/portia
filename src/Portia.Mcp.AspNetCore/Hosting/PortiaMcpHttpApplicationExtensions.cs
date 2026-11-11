using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;

namespace Cntryl.Portia;

/// <summary>Activates Portia MCP tools over ASP.NET Core Streamable HTTP.</summary>
public static class PortiaMcpHttpApplicationExtensions
{
    /// <summary>Registers stateless Streamable HTTP services for the declared Portia MCP tools.</summary>
    /// <param name="application">The shared Portia application.</param>
    /// <param name="configure">Optional HTTP transport configuration applied after Portia's stateless default. The transport must remain stateless.</param>
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
                    // The SDK disables operation scopes for stateless HTTP before this hook.
                    // Restore a per-operation scope so application services are released after each call.
                    serverOptions.ScopeRequests = true;
                };
            })
            .AddAuthorizationFilters();
        application.Services.AddOptions<HttpServerTransportOptions>()
            .Validate(options => options.Stateless,
                "Portia MCP HTTP requires stateless Streamable HTTP; stateful cancellation has not been qualified.")
            .ValidateOnStart();
        McpPrimitiveHandlers.Attach(server, application.Services);
        McpPrimitiveHandlers.MarkTransportActivated(application.Services, "AddMcpHttp");
        _ = application.Services.AddSingleton<PortiaMcpHttpMarker>();
        PortiaCrossOrigin.AddCorsDecisions(application.Services);
        return application;
    }
}
