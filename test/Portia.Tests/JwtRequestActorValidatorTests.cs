using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Cntryl.Portia;

/// <summary>
/// Verifies <see cref="JwtRequestActorValidator" /> against real, signed JWTs — not a mocked
/// <see cref="IRequestActorValidator" /> — since this is the one place the "always honor
/// expiration, even on a queue" guarantee actually gets enforced at the token level.
/// </summary>
public sealed class JwtRequestActorValidatorTests
{
    static readonly SymmetricSecurityKey SigningKey = new(
    [
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
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
        ClockSkew = TimeSpan.Zero,
    };

    /// <summary>
    /// Verifies that a well-formed, currently-valid token validates successfully and produces a
    /// principal carrying the token's claims.
    /// </summary>
    [Fact]
    public async Task ShouldValidateWellFormedToken()
    {
        var validator = new JwtRequestActorValidator(ValidationParameters);
        var token = CreateToken(expires: DateTime.UtcNow.AddMinutes(5), subject: "user-1");

        var result = await validator.ValidateAsync(token);

        Assert.True(result.IsSuccess);
        Assert.Equal("user-1", result.Value!.FindFirst("sub")?.Value);
    }

    /// <summary>
    /// Verifies the central guarantee directly: a token whose expiry has already passed fails
    /// validation — even though the token is otherwise well-formed and correctly signed. Expiry
    /// is always honored, never skipped because the token "looks fine otherwise."
    /// </summary>
    [Fact]
    public async Task ShouldRejectExpiredToken()
    {
        var validator = new JwtRequestActorValidator(ValidationParameters);
        var token = CreateToken(expires: DateTime.UtcNow.AddMinutes(-5), subject: "user-1");

        var result = await validator.ValidateAsync(token);

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Unauthorized, result.Error!.Kind);
    }

    /// <summary>
    /// Verifies that a token signed with a different key — a forged or tampered token — fails
    /// validation.
    /// </summary>
    [Fact]
    public async Task ShouldRejectTokenSignedWithWrongKey()
    {
        var validator = new JwtRequestActorValidator(ValidationParameters);
        var wrongKey = new SymmetricSecurityKey(
        [
            32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17,
            16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1,
        ]);
        var token = CreateToken(expires: DateTime.UtcNow.AddMinutes(5), subject: "user-1", signingKey: wrongKey);

        var result = await validator.ValidateAsync(token);

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Unauthorized, result.Error!.Kind);
    }

    /// <summary>
    /// Verifies that a missing token — the unauthenticated case — fails validation with a clear
    /// reason, rather than throwing or silently producing an anonymous principal.
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
        Assert.Equal(RequestErrorKind.Unauthorized, result.Error!.Kind);
    }

    /// <summary>
    /// Verifies that a syntactically malformed token (not a JWT at all) fails validation
    /// gracefully instead of throwing.
    /// </summary>
    [Fact]
    public async Task ShouldRejectMalformedToken()
    {
        var validator = new JwtRequestActorValidator(ValidationParameters);

        var result = await validator.ValidateAsync("not-a-real-token");

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Unauthorized, result.Error!.Kind);
    }

    static string CreateToken(DateTime expires, string subject, SymmetricSecurityKey? signingKey = null)
    {
        var handler = new JsonWebTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "portia-tests",
            Audience = "portia-tests-audience",
            Expires = expires,
            SigningCredentials = new SigningCredentials(signingKey ?? SigningKey, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object> { ["sub"] = subject },
        };

        return handler.CreateToken(descriptor);
    }
}
