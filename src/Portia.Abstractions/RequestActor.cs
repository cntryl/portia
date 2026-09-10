using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
///     Well-known actors for call sites that have no real caller identity to forward — an actor is
///     always required when dispatching a request (never inferred ambiently), so internal/background
///     code that was never delegated by a real user needs an explicit stand-in.
/// </summary>
public static class RequestActor
{
    /// <summary>
    ///     Gets an unauthenticated actor, for a request explicitly meant to run with no identity.
    ///     Most permission checks should reject this — it exists so "no identity" is a value you can
    ///     pass, not a null you can forget to check.
    /// </summary>
    public static ClaimsPrincipal Anonymous => new(new ClaimsIdentity());

    /// <summary>
    ///     Gets the trusted system actor, for internal/background code paths that were never
    ///     delegated by a real user (e.g. a reactor issuing a follow-up request). Distinct from a
    ///     forged user identity — code should check for this specifically where it matters, rather
    ///     than treat it as just another authenticated user.
    /// </summary>
    public static ClaimsPrincipal System => CreateSystem("portia:system");

    /// <summary>Creates an explicitly identified system principal for background execution.</summary>
    public static ClaimsPrincipal CreateSystem(string subject, string issuer = "Portia")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, subject, ClaimValueTypes.String, issuer)], "Portia.System"));
    }

    /// <summary>Whether every authenticated identity is explicitly a Portia system identity.</summary>
    public static bool IsSystem(ClaimsPrincipal actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var identities = actor.Identities.Where(identity => identity.IsAuthenticated).ToArray();
        return identities.Length > 0 && identities.All(identity => identity.AuthenticationType == "Portia.System");
    }
}
