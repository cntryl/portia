using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Selects a system principal for reactor execution, independently of the triggering event's actor.</summary>
public interface IReactorPrincipalProvider
{
    /// <summary>Gets the configured system principal for this reactor.</summary>
    /// <param name="reactor">The reactor whose executions need an identity.</param>
    /// <returns>The system principal every effect of this reactor runs as.</returns>
    ClaimsPrincipal GetPrincipal(Reactor reactor);
}
