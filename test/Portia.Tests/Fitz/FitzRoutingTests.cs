namespace Cntryl.Portia;

/// <summary>
///     Covers the segment rule that keeps a caller-supplied wildcard value from reshaping a wire
///     route. The realm is most often a tenant, so the rule also has to keep two tenants from
///     producing routes that are distinguishable to the broker but identical to a reader.
/// </summary>
public sealed class FitzRoutingTests
{
    static readonly RequestTransportCatalog Catalog = new([
        new RequestTransportRegistration(typeof(RoutingProbe), [RequestTransportId.Callable],
            new RequestRouteAttribute("*", "routing", "probe", "run"),
            new DiscriminatorAttribute("test.routing.probe"))
    ]);

    /// <summary>An ordinary ASCII realm resolves into the wire route unchanged.</summary>
    [Fact]
    public void ShouldResolveWildcardRealmGivenSuppliedRouteValues()
    {
        // Arrange
        // Act
        var route = FitzRouting.ResolveRpcRoute(Catalog, new RoutingProbe(),
            new RequestRouteValues("tenant-a"));

        // Assert
        Assert.Equal("rpc://tenant-a/routing/probe/run", route);
    }

    /// <summary>
    ///     A Cyrillic "а" is a letter, so a Unicode-aware letter test accepts it and yields a route
    ///     that reads as <c>tenant-a</c> while addressing something else. Segments are ASCII.
    /// </summary>
    [Theory]
    [InlineData("tenant-а")]
    [InlineData("тenant-a")]
    [InlineData("tenant-٠")]
    public void ShouldRejectRouteGivenNonAsciiSegment(string realm)
    {
        // Arrange
        // Act
        var thrown = Record.Exception(() =>
            FitzRouting.ResolveRpcRoute(Catalog, new RoutingProbe(), new RequestRouteValues(realm)));

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(thrown);
        Assert.Contains("unsupported characters", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>An unbounded segment is still a segment; the wire route has to stay addressable.</summary>
    [Fact]
    public void ShouldRejectRouteGivenSegmentLongerThanSupportedLength()
    {
        // Arrange
        // Act
        var thrown = Record.Exception(() =>
            FitzRouting.ResolveRpcRoute(Catalog, new RoutingProbe(),
                new RequestRouteValues(new string('a', 256))));

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(thrown);
        Assert.Contains("too long", exception.Message, StringComparison.Ordinal);
    }
}
