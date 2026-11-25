using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Verifies the identity recorded for an event.</summary>
public sealed class ActorAttributionTests
{
    /// <summary>Inconsistent identities and subjects cannot be recorded as an event actor.</summary>
    /// <param name="conflict">The source of ambiguity.</param>
    [Theory]
    [InlineData("identity")]
    [InlineData("subject")]
    [InlineData("issuer")]
    [InlineData("empty")]
    public void ShouldRejectAuthenticatedEventGivenAmbiguousAttribution(string conflict)
    {
        // Arrange
        var alice = new Claim("sub", "alice", ClaimValueTypes.String, "accounts");
        var identity = new ClaimsIdentity([alice], "bearer");
        var principal = new ClaimsPrincipal(identity);

        if (conflict == "identity")
            principal.AddIdentity(new ClaimsIdentity([alice], "another"));
        else
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, conflict switch
            {
                "subject" => "bob",
                "empty" => " ",
                _ => "alice"
            }, ClaimValueTypes.String, conflict == "issuer" ? "other" : "accounts"));

        // Act
        var exception = Record.Exception(() => ActorAttribution.FromPrincipal(principal));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Equivalent subject claims identify one actor.</summary>
    [Fact]
    public void ShouldRecordOneActorGivenEquivalentIssuedSubjects()
    {
        // Arrange
        var principal = new ClaimsPrincipal([
            new ClaimsIdentity([new Claim("sub", "ignored")]),
            new ClaimsIdentity([
                new Claim("sub", "alice", ClaimValueTypes.String, "accounts"),
                new Claim(ClaimTypes.NameIdentifier, "alice", ClaimValueTypes.String, "accounts")], "bearer")]);

        // Act
        var attribution = ActorAttribution.FromPrincipal(principal);

        // Assert
        Assert.Equal(new ActorAttribution("alice", "accounts"), attribution);
    }
}
