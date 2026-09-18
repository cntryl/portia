using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Maps an untrusted scheduled identity assertion to an application-approved system actor.</summary>
public interface IScheduledRequestActorValidator
{
    /// <summary>Validates the route and asserted system identity carried by a fired schedule.</summary>
    /// <param name="route">The concrete schedule route that fired.</param>
    /// <param name="subject">The asserted system subject.</param>
    /// <param name="issuer">The asserted system issuer.</param>
    /// <param name="ct">A token that can cancel validation.</param>
    /// <returns>The approved principal, or a bounded failure when the assertion is rejected.</returns>
    ValueTask<Result<ClaimsPrincipal>> ValidateAsync(
        string route, string subject, string issuer, CancellationToken ct = default);
}
