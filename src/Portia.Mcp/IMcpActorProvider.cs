using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Supplies the actor for an MCP ingress that has no authenticated transport principal.</summary>
public interface IMcpActorProvider
{
    /// <summary>Gets the actor for the current MCP tool invocation.</summary>
    ValueTask<ClaimsPrincipal> GetActorAsync(CancellationToken ct = default);
}
