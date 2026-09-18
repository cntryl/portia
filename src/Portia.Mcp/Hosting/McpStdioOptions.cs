using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Configures identity for MCP standard-input/output hosting.</summary>
public sealed class McpStdioOptions
{
    readonly IServiceCollection _services;

    internal McpStdioOptions(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>Uses an application-owned actor provider for every stdio invocation.</summary>
    public McpStdioOptions UseActorProvider<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    TProvider>()
        where TProvider : class, IMcpActorProvider
    {
        _ = _services.AddScoped<IMcpActorProvider, TProvider>();
        return this;
    }

    /// <summary>
    ///     Uses a visibly local-development actor. This is a convenience for a server launched by
    ///     one trusted local user and is not a production service identity.
    /// </summary>
    public McpStdioOptions UseLocalDevelopmentActor(string name = "local-mcp-user")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _ = _services.AddSingleton<IMcpActorProvider>(new LocalDevelopmentActorProvider(name));
        return this;
    }

    sealed class LocalDevelopmentActorProvider(string name) : IMcpActorProvider
    {
        readonly ClaimsPrincipal _actor = new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name)], "Portia.Mcp.LocalDevelopment"));

        public ValueTask<ClaimsPrincipal> GetActorAsync(CancellationToken ct = default)
        {
            _ = ct;
            return ValueTask.FromResult(_actor);
        }
    }
}
