namespace Cntryl.Portia;

/// <summary>
/// Verifies event-stream pattern construction.
/// </summary>
public sealed class EventStreamPatternTests
{
    /// <summary>
    /// Verifies omitted and empty segments become Fitz wildcards.
    /// </summary>
    /// <param name="realm">The optional realm.</param>
    /// <param name="area">The optional area.</param>
    /// <param name="resource">The optional resource.</param>
    /// <param name="expected">The expected Fitz selector.</param>
    [Theory]
    [InlineData("sales", "orders", "one", "stream://sales/orders/one")]
    [InlineData("sales", "orders", null, "stream://sales/orders/*")]
    [InlineData("sales", null, null, "stream://sales/*/*")]
    [InlineData(null, "orders", null, "stream://*/orders/*")]
    [InlineData("", "", "", "stream://**")]
    public void ShouldCreatePatternFromOptionalSegments(
        string? realm,
        string? area,
        string? resource,
        string expected)
    {
        var pattern = EventStreamPattern.ForPattern(realm, area, resource);

        Assert.Equal(expected, pattern.ToString());
    }
}
