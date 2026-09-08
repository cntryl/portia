using System.Text.Json.Nodes;

namespace Cntryl.Portia;

/// <summary>Exercises request context at transport boundaries.</summary>
public sealed class RequestEnvelopeContextTests
{
    /// <summary>Verifies the public transport context contract.</summary>
    [Fact]
    public void EnvelopePreservesLogicalIdentityWithoutReceiverState()
    {
        var serializer = new JsonRequestSerializer();
        var parent = new RequestContext<EnvelopeCommand>(new EnvelopeCommand(1), RequestActor.System);
        var metadata = RequestMetadata.FromParent(parent);
        var bytes = serializer.Serialize(new EnvelopeCommand(2), "opaque-token", metadata);
        var first = serializer.DeserializeEnvelope(bytes);
        var second = serializer.DeserializeEnvelope(bytes);
        Assert.Equal(metadata, first.Metadata);
        Assert.Equal(first.Metadata, second.Metadata);
        Assert.Equal("opaque-token", first.ActorToken);
        Assert.Equal(new EnvelopeCommand(2), first.Request);
        var json = JsonNode.Parse(bytes.Span)!.AsObject();
        Assert.Equal(1, json["version"]!.GetValue<int>());
        Assert.DoesNotContain("execution_id", json.ToJsonString());
        Assert.DoesNotContain("claims", json.ToJsonString());
    }

    /// <summary>Verifies W3C context is optional, round-trips, and excludes baggage.</summary>
    [Fact]
    public void ShouldRoundTripOnlyW3CFieldsGivenTraceContextWhenSerializingEnvelope()
    {
        var serializer = new JsonRequestSerializer();
        var trace = new RequestTraceContext("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", "vendor=value");

        var bytes = serializer.Serialize(new EnvelopeCommand(1), null, RequestMetadata.Create(), trace);
        var envelope = serializer.DeserializeEnvelope(bytes);
        var json = JsonNode.Parse(bytes.Span)!.AsObject();

        Assert.Equal(trace, envelope.TraceContext);
        Assert.Equal(trace.TraceParent, json["trace_parent"]!.GetValue<string>());
        Assert.Equal(trace.TraceState, json["trace_state"]!.GetValue<string>());
        Assert.DoesNotContain("baggage", json.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies the public transport context contract.</summary>
    [Fact]
    public void UnversionedEnvelopeIsRejected()
    {
        var serializer = new JsonRequestSerializer();
        var json = JsonNode.Parse(serializer.Serialize(new EnvelopeCommand(1), null).Span)!.AsObject();
        _ = json.Remove("metadata");
        _ = json.Remove("version");
        _ = Assert.Throws<InvalidOperationException>(() => serializer.DeserializeEnvelope(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    /// <summary>Verifies the public transport context contract.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidIdentityOrEnvelopeVersionIsRejected(bool unsupportedVersion)
    {
        var serializer = new JsonRequestSerializer();
        var json = JsonNode.Parse(serializer.Serialize(new EnvelopeCommand(1), null).Span)!.AsObject();
        if (unsupportedVersion)
            json["version"] = 99;
        else
            json["metadata"]!["request_id"] = Uuid.Empty.ToString();
        _ = Assert.ThrowsAny<Exception>(() => serializer.DeserializeEnvelope(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    /// <summary>Verifies the public transport context contract.</summary>
    public sealed record EnvelopeCommand(int Amount) : IRequest;
}
