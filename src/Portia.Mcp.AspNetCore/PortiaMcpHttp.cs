using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia;

/// <summary>Framework-owned MCP HTTP bootstrap used by generated application interceptors.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class PortiaMcpHttp
{
    /// <summary>Registers MCP HTTP services before building the application.</summary>
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        AddServices(builder.Services);
        return PortiaOpenApi.Build(builder);
    }

    /// <summary>Creates an application with MCP HTTP services registered.</summary>
    public static WebApplication Create(string[]? args)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        AddServices(builder.Services);
        return PortiaOpenApi.Build(builder);
    }

    static void AddServices(IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IMcpActorProvider, HttpActorProvider>();
        _ = services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .AddAuthorizationFilters();
    }

    sealed class HttpActorProvider(IHttpContextAccessor accessor) : IMcpActorProvider
    {
        public ValueTask<System.Security.Claims.ClaimsPrincipal> GetActorAsync(CancellationToken ct = default)
        {
            _ = ct;
            return ValueTask.FromResult(accessor.HttpContext?.User
                                        ?? throw new InvalidOperationException(
                                            "The MCP HTTP invocation has no active HttpContext."));
        }
    }
}
