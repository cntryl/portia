using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Cntryl.Portia;

/// <summary>
///     Validates a request's carried actor token as a JWT — signature and expiry included — and
///     turns it into the <see cref="ClaimsPrincipal" /> it represents. Depends only on
///     <see cref="Microsoft.IdentityModel" />, not ASP.NET Core, so it works the same way whether the
///     request is being dispatched from a live HTTP call or from a plain worker process with no HTTP
///     pipeline at all (a queue or schedule consumer).
///     This validates the end-user's own bearer token carried inside the request — it has nothing to
///     do with, and knows nothing about, whatever separate credentials Fitz itself might use to
///     authenticate its own RPC/queue/notice/schedule connections.
/// </summary>
/// <param name="validationParameters">
///     The parameters (issuer, audience, signing keys) every
///     token is validated against — the same ones the live API's own JWT bearer authentication is
///     configured with, so a token re-validated here is held to an identical standard.
///     <see cref="TokenValidationParameters.ClockSkew" /> is the one exception: this always
///     overrides it to <see cref="TimeSpan.Zero" /> on its own copy of
///     <paramref name="validationParameters" /> (the instance passed in is never mutated), rather
///     than trust every caller to remember to set it. Left to
///     <see cref="Microsoft.IdentityModel" />'s own default, a token that expired up to five minutes
///     ago would otherwise still validate successfully — a real, easy-to-miss gap (most JWT setup
///     guides never mention clock skew at all) this override closes unconditionally, verified
///     directly against parameters constructed the ordinary way (no explicit
///     <see cref="TokenValidationParameters.ClockSkew" /> at all) in
///     <c>JwtRequestActorValidatorTests.ShouldRejectRecentlyExpiredTokenEvenWhenCallerUsesDefaultClockSkew</c>.
/// </param>
public sealed class JwtRequestActorValidator(TokenValidationParameters validationParameters) : IRequestActorValidator
{
    static readonly JsonWebTokenHandler Handler = new();

    readonly TokenValidationParameters _validationParameters = Clone(validationParameters);

    /// <inheritdoc />
    public async ValueTask<Result<ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(token))
        {
            return Result<ClaimsPrincipal>.Failure(new RequestError(RequestErrorKind.Unauthorized,
                "No actor token was provided."));
        }

        var validationResult = await Handler.ValidateTokenAsync(token, _validationParameters).ConfigureAwait(false);

        if (!validationResult.IsValid)
        {
            // Every failure here — bad signature, wrong issuer/audience, or an expired token — is
            // the same outcome from the caller's perspective: this token no longer grants
            // authority, whether it never did or it simply doesn't any more. The library's own
            // message names the configured issuer, audience, signing key and server clock, and
            // this error travels into wire outcomes and logs, so it stays out of the result. The
            // detail belongs to whoever is diagnosing the deployment, not to the caller.
            return Result<ClaimsPrincipal>.Failure(new RequestError(
                RequestErrorKind.Unauthorized, "The actor token is not valid."));
        }
        else
        {
            return Result<ClaimsPrincipal>.Success(new ClaimsPrincipal(validationResult.ClaimsIdentity));
        }
    }

    static TokenValidationParameters Clone(TokenValidationParameters validationParameters)
    {
        ArgumentNullException.ThrowIfNull(validationParameters);

        var clone = validationParameters.Clone();
        clone.ClockSkew = TimeSpan.Zero;
        return clone;
    }
}
