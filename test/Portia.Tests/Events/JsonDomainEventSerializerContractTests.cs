using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cntryl.Portia;

/// <summary>Protects the durable JSON event envelope independently of round-trip behavior.</summary>
public sealed class JsonDomainEventSerializerContractTests
{
    /// <summary>
    ///     Verifies that the writer stays structurally compatible with the source-generated envelope
    ///     contract, including metadata and the application's payload naming policy.
    /// </summary>
    [Fact]
    public void ShouldPreserveDurableEnvelopeShape()
    {
        var options = TestJson.Options();
        var catalog = new DomainEventTypeCatalog().Register<WidgetNamed>(7, "widget.named");
        var serializer = new JsonDomainEventSerializer(catalog, null, options);
        var ev = new WidgetNamed("Sprocket");
        ev.AttachMetadata(new DomainEventMetadata(
            Parse("11111111-1111-4111-8111-111111111111"),
            Parse("22222222-2222-4222-8222-222222222222"),
            42,
            new DateTimeOffset(2026, 9, 12, 12, 34, 56, TimeSpan.Zero),
            Parse("33333333-3333-4333-8333-333333333333"),
            Parse("44444444-4444-4444-8444-444444444444"))
        {
            ExecutionId = Parse("55555555-5555-4555-8555-555555555555"),
            Actor = new ActorAttribution("operator", "portia-tests")
        });
        var expected = JsonSerializer.SerializeToUtf8Bytes(new EventEnvelope(
                "widget.named",
                7,
                JsonSerializer.SerializeToNode(ev.Metadata, PortiaCoreJsonContext.Default.DomainEventMetadata)!
                    .AsObject(),
                JsonSerializer.SerializeToNode(ev, options.GetTypeInfo(typeof(WidgetNamed)))!.AsObject()),
            PortiaCoreJsonContext.Default.EventEnvelope);

        var actual = serializer.Serialize(ev);
        var expectedNode = JsonNode.Parse(expected)!;
        var actualNode = JsonNode.Parse(actual.Span)!;
        Assert.True(JsonNode.DeepEquals(expectedNode, actualNode));

        var oldEnvelopeReader = JsonSerializer.Deserialize(
            actual.Span,
            PortiaCoreJsonContext.Default.EventEnvelope);
        Assert.NotNull(oldEnvelopeReader);
        Assert.Equal("widget.named", oldEnvelopeReader.Name);
        Assert.Equal(7, oldEnvelopeReader.Version);

        var newEnvelopeReader = Assert.IsType<WidgetNamed>(serializer.Deserialize(expected));
        Assert.Equal("Sprocket", newEnvelopeReader.Name);
        Assert.Equal(ev.Metadata, newEnvelopeReader.Metadata);
    }

    static Uuid Parse(string value) => Uuid.Parse(value, CultureInfo.InvariantCulture);
}
