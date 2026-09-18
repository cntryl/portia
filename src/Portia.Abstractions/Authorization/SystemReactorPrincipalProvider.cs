using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Uses Portia's default system principal for reactors.</summary>
public sealed class SystemReactorPrincipalProvider : IReactorPrincipalProvider
{
    /// <inheritdoc />
    public ClaimsPrincipal GetPrincipal(Reactor reactor) => RequestActor.System;
}
