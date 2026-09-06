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
