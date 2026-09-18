using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Cntryl.Portia;

/// <summary>
///     Verifies <see cref="JwtRequestActorValidator" /> against real, signed JWTs — not a mocked
///     <see cref="IRequestActorValidator" /> — since this is the one place the "always honor
///     expiration, even on a queue" guarantee actually gets enforced at the token level.
/// </summary>
public sealed class JwtRequestActorValidatorTests
{
    static readonly SymmetricSecurityKey SigningKey = new(
    [
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32
    ]);

    static readonly TokenValidationParameters ValidationParameters = new()
    {
        ValidIssuer = "portia-tests",
        ValidAudience = "portia-tests-audience",
        IssuerSigningKey = SigningKey,
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.Zero
    };

    /// <summary>
    ///     Verifies that a well-formed, currently-valid token validates successfully and produces a
    ///     principal carrying the token's claims.
    /// </summary>
    [Fact]
    public async Task ShouldValidateWellFormedToken()
    {
        var validator = new JwtRequestActorValidator(ValidationParameters);
        var token = CreateToken(DateTime.UtcNow.AddMinutes(5), "user-1");

        var result = await validator.ValidateAsync(token);

        Assert.True(result.IsSuccess);
        var actor = result.Value;
        Assert.NotNull(actor);
        Assert.Equal("user-1", actor.FindFirst("sub")?.Value);
    }

    /// <summary>
    ///     Verifies the central guarantee directly: a token whose expiry has already passed fails
    ///     validation — even though the token is otherwise well-formed and correctly signed. Expiry
    ///     is always honored, never skipped because the token "looks fine otherwise."
    /// </summary>
    [Fact]
    public async Task ShouldRejectExpiredToken()
    {
        var validator = new JwtRequestActorValidator(ValidationParameters);
        var token = CreateToken(DateTime.UtcNow.AddMinutes(-5), "user-1");

        var result = await validator.ValidateAsync(token);

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Unauthorized, result.Error.Kind);
    }

    /// <summary>
    ///     Verifies that a token signed with a different key — a forged or tampered token — fails
    ///     validation.
    /// </summary>
    [Fact]
    public async Task ShouldRejectTokenSignedWithWrongKey()
    {
        var validator = new JwtRequestActorValidator(ValidationParameters);
        var wrongKey = new SymmetricSecurityKey(
        [
            32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17,
            16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1
        ]);
        var token = CreateToken(DateTime.UtcNow.AddMinutes(5), "user-1", wrongKey);

        var result = await validator.ValidateAsync(token);

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Unauthorized, result.Error.Kind);
    }

    /// <summary>
    ///     Verifies that "expiry is always honored" holds even when the caller constructs
    ///     <see cref="TokenValidationParameters" /> the ordinary way — no explicit
    ///     <see cref="TokenValidationParameters.ClockSkew" /> at all (most JWT setup guides never
    ///     mention it). Left to <see cref="Microsoft.IdentityModel" />'s own default of five minutes,
    ///     a token that expired a minute ago would still validate successfully — confirmed as a real
    ///     gap during adversarial testing, before <see cref="JwtRequestActorValidator" /> started
    ///     unconditionally overriding it to zero on its own copy of the parameters. This is the
    ///     regression test for that fix, not a demonstration of the original gap — the whole point is
    ///     that a caller can no longer reproduce it, however they construct their own parameters.
    /// </summary>
    [Fact]
    public async Task ShouldRejectRecentlyExpiredTokenEvenWhenCallerUsesDefaultClockSkew()
    {
        var parametersWithDefaultClockSkew = new TokenValidationParameters
        {
            ValidIssuer = "portia-tests",
            ValidAudience = "portia-tests-audience",
            IssuerSigningKey = SigningKey,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true
            // ClockSkew deliberately left unset — Microsoft.IdentityModel's own default is 5
            // minutes, not zero. JwtRequestActorValidator must override this itself.
        };
        var validator = new JwtRequestActorValidator(parametersWithDefaultClockSkew);
        var token = CreateToken(DateTime.UtcNow.AddMinutes(-1), "user-1", notBefore: DateTime.UtcNow.AddMinutes(-10));

        var result = await validator.ValidateAsync(token);

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Unauthorized, result.Error.Kind);
    }

    /// <summary>
    ///     Verifies that overriding clock skew doesn't mutate the caller's own
    ///     <see cref="TokenValidationParameters" /> instance — a caller might reasonably reuse it
    ///     elsewhere (e.g. ASP.NET Core's own JWT bearer options) and would be surprised to find its
    ///     clock skew silently zeroed everywhere just because it was also handed to this class.
    /// </summary>
    [Fact]
    public void ShouldNotMutateCallersOwnValidationParameters()
    {
        var callerOwned = new TokenValidationParameters
        {
            ValidIssuer = "portia-tests",
            ValidAudience = "portia-tests-audience",
            IssuerSigningKey = SigningKey,
            ClockSkew = TimeSpan.FromMinutes(5)
        };

        _ = new JwtRequestActorValidator(callerOwned);

        Assert.Equal(TimeSpan.FromMinutes(5), callerOwned.ClockSkew);
    }

    /// <summary>
    ///     Verifies that a missing token — the unauthenticated case — fails validation with a clear
    ///     reason, rather than throwing or silently producing an anonymous principal.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ShouldRejectMissingToken(string? token)
    {
        var validator = new JwtRequestActorValidator(ValidationParameters);

        var result = await validator.ValidateAsync(token);

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Unauthorized, result.Error.Kind);
    }

    /// <summary>
    ///     Verifies that a syntactically malformed token (not a JWT at all) fails validation
    ///     gracefully instead of throwing.
    /// </summary>
    [Fact]
    public async Task ShouldRejectMalformedToken()
    {
        var validator = new JwtRequestActorValidator(ValidationParameters);

        var result = await validator.ValidateAsync("not-a-real-token");

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Unauthorized, result.Error.Kind);
    }

    /// <summary>
    ///     A rejected token must not carry the library's own diagnostics back to the caller. Those
    ///     strings name the configured issuer, audience, key identifiers, and the exact server clock,
    ///     and this message crosses transports into wire outcomes and logs.
    /// </summary>
    [Fact]
    public async Task ShouldNotDiscloseTokenValidationDiagnosticsToTheCaller()
    {
        var validator = new JwtRequestActorValidator(ValidationParameters);
        var token = CreateToken(DateTime.UtcNow.AddMinutes(-5), "user-1");

        var result = await validator.ValidateAsync(token);

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Unauthorized, result.Error.Kind);
        Assert.DoesNotContain("IDX", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("portia-tests", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>Validation observes cancellation rather than accepting a token after the caller gave up.</summary>
    [Fact]
    public async Task ShouldObserveCancellation()
    {
        var validator = new JwtRequestActorValidator(ValidationParameters);
        var token = CreateToken(DateTime.UtcNow.AddMinutes(5), "user-1");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await validator.ValidateAsync(token, cancellation.Token));
    }

    static string CreateToken(DateTime expires, string subject, SymmetricSecurityKey? signingKey = null,
        DateTime? notBefore = null)
    {
        var handler = new JsonWebTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "portia-tests",
            Audience = "portia-tests-audience",
            // Left unset, the token library defaults NotBefore to "now" — fine for every other
            // token here, but for one deliberately expiring in the past, "now" lands after
            // Expires and trips a *different* rejection ("NotBefore is after Expires") than the
            // one actually under test. Callers testing an already-expired token need to pass an
            // explicit, safely-past NotBefore to isolate the expiry check itself.
            NotBefore = notBefore,
            Expires = expires,
            SigningCredentials = new SigningCredentials(signingKey ?? SigningKey, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object> { ["sub"] = subject }
        };

        return handler.CreateToken(descriptor);
    }
}
