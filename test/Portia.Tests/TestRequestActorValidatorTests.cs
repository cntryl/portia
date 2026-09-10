namespace Cntryl.Portia;

/// <summary>
/// Verifies <see cref="TestRequestActorValidator" /> honors the same contract
/// <see cref="IRequestActorValidator.ValidateAsync" /> documents and <see cref="JwtRequestActorValidator" />
/// enforces for real: a missing token fails validation rather than silently authenticating as
/// <see cref="RequestActor.System" />. Without this, a test written against this fake could never
/// exercise a broken "no token" authorization path — the fake would substitute for the real
/// validator without honoring its contract, undermining the point of the substitution.
/// </summary>
public sealed class TestRequestActorValidatorTests
{
    /// <summary>
    /// Verifies that a <see langword="null" /> token fails validation, matching
    /// <see cref="JwtRequestActorValidator" />'s behavior for a missing token, rather than
    /// authenticating as <see cref="RequestActor.System" />.
    /// </summary>
    [Fact]
    public async Task ShouldFailValidationWhenTokenIsNull()
    {
        var validator = new TestRequestActorValidator();

        var result = await validator.ValidateAsync(null);

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Unauthorized, result.Error.Kind);
    }

    /// <summary>
    /// Verifies that any non-null token other than the configured reject token still validates
    /// successfully as <see cref="RequestActor.System" />.
    /// </summary>
    [Fact]
    public async Task ShouldSucceedAsSystemActorWhenTokenIsProvidedAndNotRejected()
    {
        var validator = new TestRequestActorValidator();

        var result = await validator.ValidateAsync("any-token");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.True(RequestActor.IsSystem(result.Value));
    }

    /// <summary>
    /// Verifies that the one token configured as <c>rejectToken</c> still fails validation, as if
    /// it had expired.
    /// </summary>
    [Fact]
    public async Task ShouldFailValidationForTheConfiguredRejectToken()
    {
        var validator = new TestRequestActorValidator(rejectToken: "expired-token");

        var result = await validator.ValidateAsync("expired-token");

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Unauthorized, result.Error.Kind);
    }
}
