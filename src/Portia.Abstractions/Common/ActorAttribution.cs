using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>A durable actor identifier, without credentials or the principal's claims.</summary>
/// <param name="Subject">The actor's stable subject identifier.</param>
/// <param name="Issuer">The authority identifying that subject.</param>
public sealed record ActorAttribution(string Subject, string Issuer)
{
    /// <summary>Reads one authenticated identity with an unambiguous, issued subject.</summary>
    /// <param name="principal">The validated principal supplied by the caller.</param>
    /// <param name="attribution">The subject and issuer shared by its NameIdentifier and sub claims.</param>
    /// <returns>True when the principal identifies one authenticated actor.</returns>
    public static bool TryFromAuthenticatedPrincipal(
        ClaimsPrincipal principal,
        [NotNullWhen(true)] out ActorAttribution? attribution
    )
    {
        attribution = Read(principal, out _);
        return attribution is not null;
    }

    internal static ActorAttribution FromPrincipal(ClaimsPrincipal principal)
    {
        var attribution = Read(principal, out var authenticated);

        if (attribution is not null)
            return attribution;

        return authenticated
            ? throw new InvalidOperationException(
                "An authenticated event actor requires exactly one authenticated identity with an unambiguous subject and issuer.")
            : new ActorAttribution("anonymous", "Portia");
    }

    static ActorAttribution? Read(ClaimsPrincipal principal, out bool authenticated)
    {
        ArgumentNullException.ThrowIfNull(principal);
        authenticated = false;
        Claim? subject = null;

        foreach (var identity in principal.Identities)
        {
            if (!identity.IsAuthenticated)
                continue;

            if (authenticated)
                return null;

            authenticated = true;

            foreach (var claim in identity.Claims)
            {
                if (claim.Type is not (ClaimTypes.NameIdentifier or "sub"))
                    continue;

                if (string.IsNullOrWhiteSpace(claim.Value) || string.IsNullOrWhiteSpace(claim.Issuer))
                    return null;

                if (subject is not null && (claim.Value != subject.Value || claim.Issuer != subject.Issuer))
                    return null;

                subject = claim;
            }
        }

        return subject is null ? null : new ActorAttribution(subject.Value, subject.Issuer);
    }
}
