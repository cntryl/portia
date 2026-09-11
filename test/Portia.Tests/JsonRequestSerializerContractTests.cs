using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cntryl.Portia;

/// <summary>
///     Verifies what the request wire format refuses. Requests cross versions of an application the way
///     events do — a rolling deploy has old senders and new receivers live at once — so a receiver that
///     accepted a contract it does not understand, or an outcome envelope that is neither a success nor
///     a failure, would dispatch something other than what was sent. Every rejection here is the
///     alternative to that.
/// </summary>
public sealed class JsonRequestSerializerContractTests
{
    /// <summary>
    ///     Verifies that a contract name the receiver has never heard of is refused by name and version,
    ///     rather than guessed at — the shape a receiver meets when a newer sender publishes a request
    ///     type this deployment does not have yet.
    /// </summary>
    [Fact]
    public void ShouldRejectUnknownRequestContractName()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var envelope = Envelope(serializer);
        envelope["contract"] = "test.shared.not-deployed-here";

        var error = Assert.Throws<InvalidOperationException>(() => serializer.DeserializeEnvelope(Bytes(envelope)));

        Assert.Contains("Unknown request contract", error.Message, StringComparison.Ordinal);
        Assert.Contains("test.shared.not-deployed-here", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that a known contract at a version this receiver does not carry is refused too. The
    ///     name alone is not the contract; accepting a newer revision under an older shape is exactly
    ///     how a field silently arrives as its default.
    /// </summary>
    [Fact]
    public void ShouldRejectKnownContractAtAnUnknownVersion()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var envelope = Envelope(serializer);
        envelope["contract_version"] = 99;

        var error = Assert.Throws<InvalidOperationException>(() => serializer.DeserializeEnvelope(Bytes(envelope)));

        Assert.Contains("version 99", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that two different CLR types cannot claim one contract name and version. A silent
    ///     win for either would make the receiver deserialize a payload into the wrong type, so the
    ///     collision is refused where it is introduced — at registration.
    /// </summary>
    [Fact]
    public void ShouldRejectTwoTypesRegisteredUnderOneContractNameAndVersion()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new JsonRequestSerializer(
        [
            Registration(typeof(UniversalAction), "test.shared.collision"),
            Registration(typeof(RpcGetValue), "test.shared.collision")
        ], TestJson.Options()));

        Assert.Contains("Duplicate request contract", error.Message, StringComparison.Ordinal);
        Assert.Contains("test.shared.collision", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that one request type cannot carry two transport descriptors. Which route and
    ///     discriminator a request uses must not depend on registration order, so the collision is
    ///     reported against the type that caused it rather than left to a dictionary's bare key error.
    /// </summary>
    [Fact]
    public void ShouldRejectTwoTransportDescriptorsForOneRequestType()
    {
        var error = Assert.Throws<ArgumentException>(() => new JsonRequestSerializer([
            Registration(typeof(UniversalAction), "test.shared.first"),
            Registration(typeof(UniversalAction), "test.shared.second")
        ], TestJson.Options()));

        Assert.Contains(nameof(UniversalAction), error.Message, StringComparison.Ordinal);
        Assert.Contains("declare each request once", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that a result envelope claiming success while carrying no value is refused rather
    ///     than handed back as a default-valued success — the caller would have no way to tell the two
    ///     apart, and a zero or null would flow on as if the handler had produced it.
    /// </summary>
    [Fact]
    public void ShouldRejectSuccessfulResultEnvelopeWithNoValue()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var envelope = new JsonObject { ["is_success"] = true };

        var error = Assert.Throws<InvalidOperationException>(() =>
            serializer.DeserializeResult<int>(Bytes(envelope)));

        Assert.Contains("requires a value", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that serializing an uninitialized outcome — a handler that fell off the end without
    ///     returning — fails loudly rather than crossing the wire as a success nobody produced.
    /// </summary>
    /// <param name="withResult">Whether the outcome carries a result value.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldRejectSerializingAnUninitializedOutcome(bool withResult)
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));

        var error = Assert.Throws<InvalidOperationException>(() => withResult
            ? serializer.SerializeResult(default(Result<int>))
            : serializer.SerializeOutcome(default));

        Assert.Contains("uninitialized", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that an envelope written by a sender speaking a different envelope revision is
    ///     refused by version, before anything inside it is read.
    /// </summary>
    [Fact]
    public void ShouldRejectUnsupportedEnvelopeVersion()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var envelope = Envelope(serializer);
        envelope["version"] = 3;

        var error = Assert.Throws<InvalidOperationException>(() => serializer.DeserializeEnvelope(Bytes(envelope)));

        Assert.Contains("version 2 only", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies the positive control: an envelope this receiver does understand round-trips to an
    ///     equal request, so the rejections above are about the defect and not the harness.
    /// </summary>
    [Fact]
    public void ShouldRoundTripAKnownContract()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));

        var envelope = serializer.DeserializeEnvelope(Bytes(Envelope(serializer)));

        Assert.Equal(new UniversalAction(7), envelope.Request);
        Assert.Equal("test.shared.universal-action", envelope.Name);
    }

    static RequestTransportRegistration Registration(Type type, string name) => new(type,
        RequestTransports.Callable, new RequestRouteAttribute("test", "shared", "collision", "run"),
        new DiscriminatorAttribute(name));

    static JsonObject Envelope(JsonRequestSerializer serializer) =>
        JsonNode.Parse(serializer.Serialize(new UniversalAction(7), null, RequestMetadata.Create(), null).Span)!
            .AsObject();

    static ReadOnlyMemory<byte> Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());
}
