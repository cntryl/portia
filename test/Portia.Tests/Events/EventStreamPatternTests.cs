namespace Cntryl.Portia;

/// <summary>
///     Verifies event-stream pattern construction.
/// </summary>
public sealed class EventStreamPatternTests
{
    /// <summary>
    ///     Verifies omitted and empty area/resource segments become Fitz wildcards.
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
    ///     Verifies cross-realm patterns cannot enter the common reader contract, because not every
    ///     event-store implementation can supply a global checkpoint.
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
            EventStreamPattern.ForPattern("sales", null, "orders"));

        Assert.Equal("resource", exception.ParamName);
    }

    /// <summary>A tenant template is distinguishable and reserves its internal realm token.</summary>
    [Fact]
    public void ShouldCreateOnlyExplicitTenantTemplates()
    {
        var template = EventStreamPattern.ForTenant("orders");

        Assert.True(template.IsTenantTemplate);
        Assert.Equal("stream://{tenant}/orders/*", template.ToString());
        Assert.Throws<ArgumentException>(() => EventStreamPattern.ForPattern("{tenant}", "orders"));
    }

    /// <summary>Binding occurs before the exact pattern is used for checkpoint identity or reads.</summary>
    [Fact]
    public void ShouldBindTenantTemplateToTheWorkloadTenant()
    {
        var projector = new TestProjector(new RecordingProjectionTarget(),
            EventStreamPattern.ForTenant("orders"));

        projector.BindWorkload(new WorkloadIdentity("orders", new TenantId("acme")), null);

        Assert.False(projector.Pattern.IsTenantTemplate);
        Assert.Equal("stream://acme/orders/*", projector.Pattern.ToString());
    }

    /// <summary>Manual runners cannot construct a checkpoint for an unbound template.</summary>
    [Fact]
    public async Task ShouldRejectManualRunOfUnboundTenantTemplate()
    {
        var projector = new TestProjector(new RecordingProjectionTarget(),
            EventStreamPattern.ForTenant("orders"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProjectorRunner(new InMemoryEventStore())
                .RunAsync(projector, ProjectionCheckpoint.Start).AsTask());

        Assert.Contains("unbound tenant stream template", error.Message, StringComparison.Ordinal);
    }

    /// <summary>An exact pattern cannot be silently rebound to a tenant.</summary>
    [Fact]
    public void ShouldRejectBindingExactPatternToTenantWorkload()
    {
        var projector = new TestProjector(new RecordingProjectionTarget(),
            EventStreamPattern.ForPattern("placeholder", "orders"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            projector.BindWorkload(new WorkloadIdentity("orders", new TenantId("acme")), null));

        Assert.Contains("Only an unbound tenant stream template", error.Message, StringComparison.Ordinal);
    }
}
