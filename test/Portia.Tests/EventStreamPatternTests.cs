namespace Cntryl.Portia;

/// <summary>
/// Verifies event-stream pattern construction.
/// </summary>
public sealed class EventStreamPatternTests
{
    /// <summary>
    /// Verifies omitted and empty area/resource segments become Fitz wildcards.
    /// </summary>
    /// <param name="realm">The required realm.</param>
    /// <param name="area">The optional area.</param>
    /// <param name="resource">The optional resource.</param>
    /// <param name="expected">The expected Fitz selector.</param>
    [Theory]
    [InlineData("sales", "orders", "one", "stream://sales/orders/one")]
    [InlineData("sales", "orders", null, "stream://sales/orders/*")]
    [InlineData("sales", null, null, "stream://sales/*/*")]
    public void ShouldCreatePatternFromOptionalSegments(
        string realm,
        string? area,
        string? resource,
        string expected)
    {
        var pattern = EventStreamPattern.ForPattern(realm, area, resource);

        Assert.Equal(expected, pattern.ToString());
    }

    /// <summary>
    /// Verifies cross-realm patterns cannot enter the common reader contract, because not every
    /// event-store implementation can supply a global checkpoint.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ShouldRejectMissingRealm(string? realm)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => EventStreamPattern.ForPattern(realm!));

        Assert.Equal("realm", exception.ParamName);
    }

    /// <summary>A resource cannot be selected beneath a wildcard area.</summary>
    [Fact]
    public void ShouldRejectResourceWithoutArea()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            EventStreamPattern.ForPattern("sales", area: null, resource: "orders"));

        Assert.Equal("resource", exception.ParamName);
    }
}
