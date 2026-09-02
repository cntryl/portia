using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Cntryl.Portia;

/// <summary>
/// Validates a request's carried actor token as a JWT — signature and expiry included — and
/// turns it into the <see cref="ClaimsPrincipal" /> it represents. Depends only on
/// <see cref="Microsoft.IdentityModel" />, not ASP.NET Core, so it works the same way whether the
/// request is being dispatched from a live HTTP call or from a plain worker process with no HTTP
/// pipeline at all (a queue or schedule consumer).
///
/// This validates the end-user's own bearer token carried inside the request — it has nothing to
/// do with, and knows nothing about, whatever separate credentials Fitz itself might use to
/// authenticate its own RPC/queue/notice/schedule connections.
/// </summary>
/// <param name="validationParameters">The parameters (issuer, audience, signing keys, clock skew)
/// every token is validated against — the same ones the live API's own JWT bearer authentication
/// is configured with, so a token re-validated here is held to an identical standard.</param>
public sealed class JwtRequestActorValidator(TokenValidationParameters validationParameters) : IRequestActorValidator
{
    static readonly JsonWebTokenHandler Handler = new();

    readonly TokenValidationParameters _validationParameters = validationParameters
        ?? throw new ArgumentNullException(nameof(validationParameters));

    /// <inheritdoc />
    public async ValueTask<Result<ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Result<ClaimsPrincipal>.Failure(new RequestError(RequestErrorKind.Unauthorized, "No actor token was provided."));

        var validationResult = await Handler.ValidateTokenAsync(token, _validationParameters).ConfigureAwait(false);

        if (!validationResult.IsValid)
        {
            // Every failure here — bad signature, wrong issuer/audience, or an expired token —
            // is the same outcome from the caller's perspective: this token no longer grants
            // authority, whether it never did or it simply doesn't any more.
            return Result<ClaimsPrincipal>.Failure(new RequestError(
                RequestErrorKind.Unauthorized,
                $"Actor token failed validation: {validationResult.Exception?.Message ?? "invalid token."}"));
        }

        return Result<ClaimsPrincipal>.Success(new ClaimsPrincipal(validationResult.ClaimsIdentity));
    }
}
