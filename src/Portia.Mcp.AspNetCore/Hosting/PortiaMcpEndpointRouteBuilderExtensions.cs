using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        if (endpoints.ServiceProvider.GetService<PortiaMcpHttpMarker>() is null)
            throw new InvalidOperationException(
                "MapPortiaMcp requires AddMcpHttp() in the shared Portia application composition.");
        if (!endpoints.ServiceProvider.GetServices<McpToolRegistration>().Any())
            throw new InvalidOperationException(
                "MapPortiaMcp requires at least one AddMcpTool<TRequest>() declaration.");
        var maximum = endpoints.ServiceProvider.GetService<IOptions<PortiaHttpOptions>>()?.Value.MaxJsonBodyBytes
                      ?? PortiaHttpOptions.DefaultMaxJsonBodyBytes;
        if (maximum <= 0)
            throw new InvalidOperationException($"{nameof(PortiaHttpOptions.MaxJsonBodyBytes)} must be positive.");
        var builder = endpoints.MapMcp(pattern).WithMetadata(new McpRequestSizeLimit(maximum));
        // Streamable HTTP must validate the browser origin; the MCP endpoints share Portia's rule.
        builder.Finally(endpoint =>
        {
            if (endpoint.RequestDelegate is not { } next)
                return;
            endpoint.RequestDelegate = context => PortiaHttpBinding.RejectCrossOrigin(context) is { } rejection
                ? rejection.ExecuteAsync(context)
                : next(context);
        });
        return builder;
    }

    sealed record McpRequestSizeLimit(long? MaxRequestBodySize) : IRequestSizeLimitMetadata;
}
