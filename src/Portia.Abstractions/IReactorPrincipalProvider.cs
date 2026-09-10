using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Selects a system principal for reactor execution, independently of the triggering event's actor.</summary>
public interface IReactorPrincipalProvider
{
    /// <summary>Gets the configured system principal for this reactor.</summary>
    ClaimsPrincipal GetPrincipal(Reactor reactor);
}
