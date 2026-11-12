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
        // Arrange
        // Act
        var pattern = EventStreamPattern.ForPattern(realm, area, resource);

        // Assert
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
        // Arrange
        // Act
        var thrown = Record.Exception(() => EventStreamPattern.ForPattern(realm!));

        // Assert
        var exception = Assert.IsAssignableFrom<ArgumentException>(thrown);
        Assert.Equal("realm", exception.ParamName);
    }

    /// <summary>A resource cannot be selected beneath a wildcard area.</summary>
    [Fact]
    public void ShouldRejectResourceWithoutArea()
    {
        // Arrange
        // Act
        var thrown = Record.Exception(() =>
            EventStreamPattern.ForPattern("sales", null, "orders"));

        // Assert
        var exception = Assert.IsType<ArgumentException>(thrown);
        Assert.Equal("resource", exception.ParamName);
    }

    /// <summary>A tenant template is distinguishable and reserves its internal realm token.</summary>
    [Fact]
    public void ShouldCreateOnlyExplicitTenantTemplates()
    {
        // Arrange
        // Act
        var template = EventStreamPattern.ForTenant("orders");

        // Assert
        Assert.True(template.IsTenantTemplate);
        Assert.Equal("stream://{tenant}/orders/*", template.ToString());
    }

    /// <summary>The internal tenant placeholder cannot be supplied as an ordinary realm.</summary>
    [Fact]
    public void ShouldRejectTenantPlaceholderRealmGivenForPattern()
    {
        // Arrange
        // Act
        var exception = Record.Exception(() => EventStreamPattern.ForPattern("{tenant}", "orders"));

        // Assert
        _ = Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Binding occurs before the exact pattern is used for checkpoint identity or reads.</summary>
    [Fact]
    public void ShouldBindTenantTemplateToTheWorkloadTenant()
    {
        // Arrange
        var projector = new TestProjector(new RecordingProjectionTarget(),
            EventStreamPattern.ForTenant("orders"));

        // Act
        projector.BindWorkload(new WorkloadIdentity("orders", new TenantId("acme")), null);

        // Assert
        Assert.False(projector.Pattern.IsTenantTemplate);
        Assert.Equal("stream://acme/orders/*", projector.Pattern.ToString());
    }

    /// <summary>A template cannot be bound to its own reserved realm token, which would read as still unbound.</summary>
    [Fact]
    public void ShouldRejectBindingTenantTemplateToReservedRealm() =>
        _ = Assert.ThrowsAny<ArgumentException>(() =>
            EventStreamPattern.ForTenant("orders").BindTenant(new TenantId("{tenant}")));

    /// <summary>A tenant ID is refused unless it is a valid realm segment, however it is assigned.</summary>
    /// <param name="value">The invalid tenant ID.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/b*")]
    [InlineData("{tenant}")]
    public void ShouldRejectTenantIdThatIsNotARealmSegment(string? value)
    {
        // Arrange
        // Act
        var constructed = Record.Exception(() => new TenantId(value!));
        var replaced = Record.Exception(() => new TenantId("acme") with { Value = value! });

        // Assert
        _ = Assert.IsAssignableFrom<ArgumentException>(constructed);
        _ = Assert.IsAssignableFrom<ArgumentException>(replaced);
    }

    /// <summary>An uninitialized tenant ID still formats as a string, as its non-nullable signature promises.</summary>
    [Fact]
    public void ShouldFormatDefaultTenantIdAsEmpty() => Assert.Equal(string.Empty, default(TenantId).ToString());

    /// <summary>Manual runners cannot construct a checkpoint for an unbound template.</summary>
    [Fact]
    public async Task ShouldRejectManualRunOfUnboundTenantTemplate()
    {
        // Arrange
        var projector = new TestProjector(new RecordingProjectionTarget(),
            EventStreamPattern.ForTenant("orders"));

        // Act
        var thrown = await Record.ExceptionAsync(() =>
            new ProjectorRunner(new InMemoryEventStore())
                .RunAsync(projector, ProjectionCheckpoint.Start).AsTask());

        // Assert
        var error = Assert.IsType<InvalidOperationException>(thrown);
        Assert.Contains("unbound tenant stream template", error.Message, StringComparison.Ordinal);
    }

    /// <summary>An exact pattern cannot be silently rebound to a tenant.</summary>
    [Fact]
    public void ShouldRejectBindingExactPatternToTenantWorkload()
    {
        // Arrange
        var projector = new TestProjector(new RecordingProjectionTarget(),
            EventStreamPattern.ForPattern("placeholder", "orders"));

        // Act
        var thrown = Record.Exception(() =>
            projector.BindWorkload(new WorkloadIdentity("orders", new TenantId("acme")), null));

        // Assert
        var error = Assert.IsType<InvalidOperationException>(thrown);
        Assert.Contains("Only an unbound tenant stream template", error.Message, StringComparison.Ordinal);
    }
}
