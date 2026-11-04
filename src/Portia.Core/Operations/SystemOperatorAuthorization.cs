using System.Security.Claims;
using Microsoft.Extensions.Configuration;

namespace Cntryl.Portia;

static class SystemOperatorAuthorization
{
    internal static bool IsAdministrator(ClaimsPrincipal actor, IConfiguration? configuration)
    {
        var role = configuration?["PORTIA_SYSTEM_ADMIN_ROLE"] ??
                   Environment.GetEnvironmentVariable("PORTIA_SYSTEM_ADMIN_ROLE");
        return !string.IsNullOrWhiteSpace(role) && actor.Identities.Any(identity =>
            identity.IsAuthenticated && identity.HasClaim(identity.RoleClaimType, role));
    }
}
