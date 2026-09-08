using System.Text.Json.Nodes;

namespace Cntryl.Portia;

/// <summary>
/// Verifies <see cref="JsonDomainEventSerializer" />'s two evolution paths: an upcaster chain
/// bridging a stored schema version forward to whatever the catalog currently has registered,
/// and an old CLR type staying directly registered (and so directly readable, no upcasting at
/// all) alongside its replacement.
/// </summary>
public sealed class EventSchemaEvolutionTests
{
    /// <summary>
    /// Verifies a plain round trip using an explicit <see cref="DiscriminatorAttribute" />
    /// to its CLR type name and schema version 1, and serializes/deserializes with no upcasting.
    /// </summary>
    [Fact]
    public void ShouldRoundTripEventWithDefaultSchemaIdentity()
    {
        var serializer = TestJson.DomainSerializer(new DomainEventTypeCatalog().Register<WidgetNamed>(1, "WidgetNamed"));
        var original = Committed(new WidgetNamed("Sprocket"));

        var deserialized = serializer.Deserialize(serializer.Serialize(original));

        var widget = Assert.IsType<WidgetNamed>(deserialized);
        Assert.Equal("Sprocket", widget.Name);
        Assert.Equal(original.Metadata.EventId, widget.Metadata.EventId);
    }

    /// <summary>
    /// Verifies that an event stored at schema version 1, with only the version-2 replacement
    /// type registered in the catalog, is upcast forward through a registered
    /// <see cref="IJsonDomainEventUpcaster" /> before being deserialized into the current type.
    /// </summary>
    [Fact]
    public void ShouldUpcastEventWhenOnlyLaterSchemaVersionIsRegistered()
    {
        var writer = TestJson.DomainSerializer(new DomainEventTypeCatalog().Register<WidgetNamed>(1, "WidgetNamed"));
        var stored = writer.Serialize(Committed(new WidgetNamed("Sprocket")));

        var reader = TestJson.DomainSerializer(
            new DomainEventTypeCatalog().Register<WidgetRenamed>(2, "WidgetNamed"),
            [new WidgetNamedToRenamedUpcaster()]);

        var deserialized = reader.Deserialize(stored);

        var widget = Assert.IsType<WidgetRenamed>(deserialized);
        Assert.Equal("Sprocket", widget.DisplayName);
    }

    /// <summary>
    /// Verifies that an event whose exact stored schema version is still registered in the
    /// catalog deserializes directly into its own (superseded) CLR type — no upcasting runs, so
    /// old and new versions can simply coexist forever with no upcaster written at all.
    /// </summary>
    [Fact]
    public void ShouldResolveDirectlyWhenExactSchemaVersionIsStillRegistered()
    {
        var v1Writer = TestJson.DomainSerializer(new DomainEventTypeCatalog().Register<OrderPlacedV1>(1, "OrderPlaced"));
        var v2Writer = TestJson.DomainSerializer(new DomainEventTypeCatalog().Register<OrderPlacedV2>(2, "OrderPlaced"));
        var storedV1 = v1Writer.Serialize(Committed(new OrderPlacedV1(100)));
        var storedV2 = v2Writer.Serialize(Committed(new OrderPlacedV2(100, "USD")));

        var reader = TestJson.DomainSerializer(
            new DomainEventTypeCatalog().Register<OrderPlacedV1>(1, "OrderPlaced").Register<OrderPlacedV2>(2, "OrderPlaced"));

        var deserializedV1 = Assert.IsType<OrderPlacedV1>(reader.Deserialize(storedV1));
        var deserializedV2 = Assert.IsType<OrderPlacedV2>(reader.Deserialize(storedV2));
        Assert.Equal(100, deserializedV1.AmountCents);
        Assert.Equal(100, deserializedV2.AmountCents);
        Assert.Equal("USD", deserializedV2.Currency);
    }

    /// <summary>
    /// Verifies that a stored schema version with neither a catalog registration nor an upcaster
    /// to bridge it forward fails loudly rather than silently dropping data.
    /// </summary>
    [Fact]
    public void ShouldThrowWhenNoRegistrationOrUpcasterCanResolveStoredVersion()
    {
        var writer = TestJson.DomainSerializer(new DomainEventTypeCatalog().Register<WidgetNamed>(1, "WidgetNamed"));
        var stored = writer.Serialize(Committed(new WidgetNamed("Sprocket")));

        var reader = TestJson.DomainSerializer(new DomainEventTypeCatalog().Register<WidgetRenamed>(2, "WidgetNamed"));

        var exception = Assert.Throws<InvalidOperationException>(() => reader.Deserialize(stored));
        Assert.Contains("WidgetNamed", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that registering two upcasters for the same event name and source version fails
    /// fast at construction.
    /// </summary>
    [Fact]
    public void ShouldThrowWhenRegisteringDuplicateUpcasterForSameNameAndVersion() =>
        Assert.Throws<InvalidOperationException>(() => TestJson.DomainSerializer(
            new DomainEventTypeCatalog().Register<WidgetRenamed>(2, "WidgetNamed"),
            [new WidgetNamedToRenamedUpcaster(), new WidgetNamedToRenamedUpcaster()]));

    static T Committed<T>(T ev)
        where T : DomainEvent
    {
        ev.AttachMetadata(new DomainEventMetadata(
            Uuid.CreateVersion4(),
            Uuid.CreateVersion4(),
            1,
            DateTimeOffset.UtcNow));
        return ev;
    }
}

[Discriminator("WidgetNamed")]
sealed record WidgetNamed(string Name) : DomainEvent;

[Discriminator("WidgetNamed", 2)]
sealed record WidgetRenamed(string DisplayName) : DomainEvent;

sealed class WidgetNamedToRenamedUpcaster : IJsonDomainEventUpcaster
{
    public string EventName => "WidgetNamed";

    public int FromVersion => 1;

    public JsonObject Upcast(JsonObject payload) => new()
    {
        ["display_name"] = payload["name"]?.GetValue<string>(),
    };
}

[Discriminator("OrderPlaced")]
sealed record OrderPlacedV1(int AmountCents) : DomainEvent;

[Discriminator("OrderPlaced", 2)]
sealed record OrderPlacedV2(int AmountCents, string Currency) : DomainEvent;
