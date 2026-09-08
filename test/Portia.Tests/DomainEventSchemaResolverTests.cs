using System.Text.Json.Nodes;

namespace Cntryl.Portia;

/// <summary>
/// Verifies <see cref="DomainEventSchemaResolver" /> — the upcast-until-resolvable walk extracted
/// out of <see cref="JsonDomainEventSerializer" /> so that class only handles JSON envelope
/// (de)serialization, not schema-evolution policy.
/// </summary>
public sealed class DomainEventSchemaResolverTests
{
    /// <summary>
    /// Verifies a name/version already registered in the catalog resolves directly, with the
    /// payload unchanged.
    /// </summary>
    [Fact]
    public void ShouldResolveDirectlyWhenExactVersionIsRegistered()
    {
        var resolver = new DomainEventSchemaResolver(
            new DomainEventTypeCatalog().Register<WidgetNamed>(1, "WidgetNamed"),
            new Dictionary<(string, int), IJsonDomainEventUpcaster>());
        var payload = new JsonObject { ["name"] = "Sprocket" };

        var (type, resolvedPayload) = resolver.Resolve("WidgetNamed", 1, payload);

        Assert.Equal(typeof(WidgetNamed), type);
        Assert.Same(payload, resolvedPayload);
    }

    /// <summary>
    /// Verifies an unregistered version is walked forward through a registered upcaster until it
    /// reaches a version the catalog has.
    /// </summary>
    [Fact]
    public void ShouldWalkForwardThroughAnUpcasterUntilCatalogResolves()
    {
        var upcaster = new WidgetNamedToRenamedUpcaster();
        var resolver = new DomainEventSchemaResolver(
            new DomainEventTypeCatalog().Register<WidgetRenamed>(2, "WidgetNamed"),
            new Dictionary<(string, int), IJsonDomainEventUpcaster> { [(upcaster.EventName, upcaster.FromVersion)] = upcaster });
        var payload = new JsonObject { ["name"] = "Sprocket" };

        var (type, resolvedPayload) = resolver.Resolve("WidgetNamed", 1, payload);

        Assert.Equal(typeof(WidgetRenamed), type);
        Assert.Equal("Sprocket", resolvedPayload["display_name"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies a version with neither a catalog registration nor an upcaster fails loudly.
    /// </summary>
    [Fact]
    public void ShouldThrowWhenNoRegistrationOrUpcasterCanResolveVersion()
    {
        var resolver = new DomainEventSchemaResolver(
            new DomainEventTypeCatalog().Register<WidgetRenamed>(2, "WidgetNamed"),
            new Dictionary<(string, int), IJsonDomainEventUpcaster>());

        _ = Assert.Throws<InvalidOperationException>(() => resolver.Resolve("WidgetNamed", 1, []));
    }
}
