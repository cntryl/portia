using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Selects a system principal for reactor execution, independently of the triggering event's actor.</summary>
public interface IReactorPrincipalProvider
{
    /// <summary>Gets the configured system principal for this reactor.</summary>
    ClaimsPrincipal GetPrincipal(Reactor reactor);
}

/// <summary>Uses Portia's default system principal for reactors.</summary>
public sealed class SystemReactorPrincipalProvider : IReactorPrincipalProvider
{
    /// <inheritdoc />
    public ClaimsPrincipal GetPrincipal(Reactor reactor) => RequestActor.System;
}
