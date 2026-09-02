using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
/// Verifies that every <see cref="DomainEvent" /> type declared anywhere in the compilation is
/// registered into a <see cref="DomainEventTypeCatalog" /> by generated code — no per-type
/// <c>.Register&lt;T&gt;()</c> call to remember, matching the zero-boilerplate discovery already
/// used for reactors, projectors, and request transports.
/// </summary>
public sealed class DomainEventCatalogGeneratorTests
{
    /// <summary>
    /// Verifies that a plain, default-schema event type is registered under its CLR type name at
    /// version 1, and that a type carrying an explicit <see cref="EventSchemaAttribute" /> is
    /// registered under its declared name and version.
    /// </summary>
    [Fact]
    public void ShouldRegisterEveryDomainEventTypeInCompilation()
    {
        var catalog = new DomainEventTypeCatalog().AddPortiaGeneratedDomainEvents();

        Assert.True(catalog.TryResolve("WidgetNamed", 1, out var widgetType));
        Assert.Equal(typeof(WidgetNamed), widgetType);

        Assert.True(catalog.TryResolve("OrderPlaced", 2, out var orderType));
        Assert.Equal(typeof(OrderPlacedV2), orderType);
    }

    /// <summary>
    /// Verifies that a fully wired <see cref="IDomainEventSerializer" /> is resolvable from DI
    /// after one generated call — the catalog, its population, and the serializer itself are all
    /// zero-boilerplate.
    /// </summary>
    [Fact]
    public void ShouldResolveDomainEventSerializerAfterGeneratedRegistration()
    {
        var services = new ServiceCollection();
        _ = services.AddPortiaGeneratedDomainEventSerialization();
        using var provider = services.BuildServiceProvider();

        var serializer = provider.GetRequiredService<IDomainEventSerializer>();

        Assert.NotNull(serializer);
    }

    /// <summary>
    /// End-to-end proof for schema evolution through the DI-resolved, generator-populated
    /// <see cref="IDomainEventSerializer" />: bytes representing a schema version whose CLR type
    /// no longer exists in the codebase (so the generated catalog has nothing to auto-register
    /// for it) are still upcast forward, via an app-registered <see cref="IDomainEventUpcaster" />
    /// picked up automatically from DI's <c>IEnumerable&lt;IDomainEventUpcaster&gt;</c> — no
    /// manual serializer construction, matching how <see cref="FitzEventStore" /> would resolve
    /// one in a real app.
    /// </summary>
    [Fact]
    public void ShouldUpcastThroughGeneratedSerializerWhenUpcasterIsRegisteredInDi()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

        // Simulates bytes durably stored back when a "Gadget" v1 type still existed in the
        // codebase — that type is gone now, so nothing in this compilation can register it, and
        // the only way forward is the upcaster below.
        var metadata = new DomainEventMetadata(Uuid.CreateVersion7(), Uuid.CreateVersion7(), 1, DateTimeOffset.UtcNow);
        var envelope = new JsonObject
        {
            ["name"] = "Gadget",
            ["version"] = 1,
            ["metadata"] = JsonSerializer.SerializeToNode(metadata, options),
            ["payload"] = new JsonObject { ["name"] = "Sprocket" },
        };
        var stored = JsonSerializer.SerializeToUtf8Bytes(envelope, options);

        var services = new ServiceCollection();
        _ = services.AddSingleton<IDomainEventUpcaster, GadgetV1ToV2Upcaster>();
        _ = services.AddPortiaGeneratedDomainEventSerialization();
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IDomainEventSerializer>();

        var deserialized = reader.Deserialize(stored);

        var gadget = Assert.IsType<GadgetRenamed>(deserialized);
        Assert.Equal("Sprocket", gadget.DisplayName);
    }
}

[EventSchema("Gadget", 2)]
sealed record GadgetRenamed(string DisplayName) : DomainEvent;

sealed class GadgetV1ToV2Upcaster : IDomainEventUpcaster
{
    public string EventName => "Gadget";

    public int FromVersion => 1;

    public JsonObject Upcast(JsonObject payload) => new()
    {
        ["display_name"] = payload["name"]?.GetValue<string>(),
    };
}
