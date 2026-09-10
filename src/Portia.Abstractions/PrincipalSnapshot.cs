using System.Security.Claims;

namespace Cntryl.Portia;

static class PrincipalSnapshot
{
    public static ClaimsPrincipal Copy(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new ClaimsPrincipal(principal.Identities.Select(identity => new ClaimsIdentity(identity)));
    }
}
