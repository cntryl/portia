using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>A durable actor identifier, without credentials or the principal's claims.</summary>
/// <param name="Subject">The actor's stable subject identifier.</param>
/// <param name="Issuer">The authority identifying that subject.</param>
public sealed record ActorAttribution(string Subject, string Issuer)
{
    internal static ActorAttribution FromPrincipal(ClaimsPrincipal principal)
    {
        var identity = principal.Identities.FirstOrDefault(value => value.IsAuthenticated);
        if (identity == null)
        {
            return new ActorAttribution("anonymous", "Portia");
        }
        else
        {
            var subject = identity.FindFirst(ClaimTypes.NameIdentifier) ?? identity.FindFirst("sub");
            return subject is null || string.IsNullOrWhiteSpace(subject.Value) ||
                   string.IsNullOrWhiteSpace(subject.Issuer)
                ? throw new InvalidOperationException(
                    "An authenticated event actor requires a NameIdentifier or sub claim with a stable subject and issuer.")
                : new ActorAttribution(subject.Value, subject.Issuer);
        }
    }
}
