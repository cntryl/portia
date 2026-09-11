namespace Cntryl.Portia.Consumer;

/// <summary>
///     Covers which declarations the domain-event catalog generator will emit a registration for.
/// </summary>
public sealed class DomainEventCatalogShapeTests
{
    /// <summary>
    ///     Verifies an open generic event type does not reach the generated catalog. It cannot be
    ///     named as a closed type argument, so emitting one produces code that does not compile —
    ///     a build break in generated source rather than a diagnostic on the declaration.
    /// </summary>
    [Fact]
    public void ShouldNotEmitCatalogRegistrationForAGenericDomainEvent()
    {
        const string source = """
                              using Cntryl.Portia;

                              [Discriminator("generic.event", 1)]
                              public sealed record GenericEvent<T>(T Value) : DomainEvent;

                              [Discriminator("closed.event", 1)]
                              public sealed record ClosedEvent : DomainEvent;
                              """;

        var generated = GeneratorCompilation.GeneratedSource(source, new DomainEventCatalogGenerator());

        Assert.Contains("ClosedEvent", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("GenericEvent", generated, StringComparison.Ordinal);
    }
}
