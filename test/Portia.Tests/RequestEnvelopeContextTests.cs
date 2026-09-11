using System.Text;
using System.Text.Json.Nodes;

namespace Cntryl.Portia;

/// <summary>Exercises request context at transport boundaries.</summary>
public sealed class RequestEnvelopeContextTests
{
    /// <summary>
    ///     Verifies that an envelope carries the request's logical identity — metadata, actor token,
    ///     contract name and version — identically on every deserialization, while receiver-only state
    ///     (the execution id and the resolved principal's claims) never crosses the wire at all.
    /// </summary>
    [Fact]
    public void ShouldPreserveLogicalIdentityWithoutReceiverStateWhenDeserializingEnvelope()
    {
        var serializer = TestJson.Serializer(typeof(EnvelopeCommand));
        var parent = new RequestContext<EnvelopeCommand>(new EnvelopeCommand(1), RequestActor.System);
        var metadata = RequestMetadata.FromParent(parent);
        var bytes = serializer.Serialize(new EnvelopeCommand(2), "opaque-token", metadata, null);
        var first = serializer.DeserializeEnvelope(bytes);
        var second = serializer.DeserializeEnvelope(bytes);
        Assert.Equal(metadata, first.Metadata);
        Assert.Equal(first.Metadata, second.Metadata);
        Assert.Equal("opaque-token", first.ActorToken);
        Assert.Equal(new EnvelopeCommand(2), first.Request);
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(bytes.Span));
        Assert.Equal(2, Assert.IsType<JsonValue>(json["version"], exactMatch: false).GetValue<int>());
        Assert.Equal("test.EnvelopeCommand", Assert.IsType<JsonValue>(json["contract"], exactMatch: false).GetValue<string>());
        Assert.Equal(1, Assert.IsType<JsonValue>(json["contract_version"], exactMatch: false).GetValue<int>());
        Assert.DoesNotContain("execution_id", json.ToJsonString());
        Assert.DoesNotContain("claims", json.ToJsonString());
    }

    /// <summary>Verifies W3C context is optional, round-trips, and excludes baggage.</summary>
    [Fact]
    public void ShouldRoundTripOnlyW3CFieldsGivenTraceContextWhenSerializingEnvelope()
    {
        var serializer = TestJson.Serializer(typeof(EnvelopeCommand));
        var trace = new RequestTraceContext("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", "vendor=value");

        var bytes = serializer.Serialize(new EnvelopeCommand(1), null, RequestMetadata.Create(), trace);
        var envelope = serializer.DeserializeEnvelope(bytes);
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(bytes.Span));

        Assert.Equal(trace, envelope.TraceContext);
        Assert.Equal(trace.TraceParent, Assert.IsType<JsonValue>(json["traceparent"], exactMatch: false).GetValue<string>());
        Assert.Equal(trace.TraceState, Assert.IsType<JsonValue>(json["tracestate"], exactMatch: false).GetValue<string>());
        Assert.DoesNotContain("baggage", json.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Verifies that an envelope with no version and no metadata — the pre-envelope wire shape — is
    ///     rejected outright rather than deserialized into a request with a default-constructed identity.
    /// </summary>
    [Fact]
    public void ShouldRejectEnvelopeWithoutVersionOrMetadata()
    {
        var serializer = TestJson.Serializer(typeof(EnvelopeCommand));
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(
            serializer.Serialize(new EnvelopeCommand(1), null, RequestMetadata.Create(), null).Span));
        _ = json.Remove("metadata");
        _ = json.Remove("version");
        _ = Assert.Throws<InvalidOperationException>(() =>
            serializer.DeserializeEnvelope(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    /// <summary>
    ///     Verifies that an envelope version this receiver does not understand, and an empty request id,
    ///     both fail deserialization instead of being accepted with whatever the sender happened to send.
    /// </summary>
    /// <param name="unsupportedVersion">
    ///     <see langword="true" /> to corrupt the envelope version; otherwise the request identity.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldRejectUnsupportedEnvelopeVersionOrEmptyRequestIdentity(bool unsupportedVersion)
    {
        var serializer = TestJson.Serializer(typeof(EnvelopeCommand));
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(
            serializer.Serialize(new EnvelopeCommand(1), null, RequestMetadata.Create(), null).Span));
        if (unsupportedVersion)
        {
            json["version"] = 99;
        }
        else
        {
            var metadata = Assert.IsType<JsonObject>(json["metadata"]);
            metadata["request_id"] = Uuid.Empty.ToString();
        }

        _ = Assert.ThrowsAny<Exception>(() =>
            serializer.DeserializeEnvelope(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    /// <summary>A request used only by these envelope tests.</summary>
    /// <param name="Amount">An arbitrary payload value, so two instances can be told apart.</param>
    public sealed record EnvelopeCommand(int Amount) : IRequest;
}
