using System.Text;
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
    /// <summary>Every required envelope member is a permanent poison-message failure when absent.</summary>
    [Theory]
    [InlineData("version")]
    [InlineData("contract")]
    [InlineData("contract_version")]
    [InlineData("metadata")]
    [InlineData("payload")]
    public void ShouldClassifyMissingRequiredRequestEnvelopePropertiesAsPermanent(string property)
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var envelope = Envelope(serializer);
        _ = envelope.Remove(property);

        var error = Assert.Throws<InvalidOperationException>(() => serializer.DeserializeEnvelope(Bytes(envelope)));

        Assert.Equal(RequestEnvelopeFailureKind.Permanent, RequestEnvelopeFailure.GetKind(error));
    }

    /// <summary>Malformed JSON keeps its JsonException contract while carrying permanent classification.</summary>
    [Fact]
    public void ShouldClassifyMalformedJsonAsPermanentWithoutReplacingJsonException()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));

        var error = Assert.ThrowsAny<System.Text.Json.JsonException>(() =>
            serializer.DeserializeEnvelope(Encoding.UTF8.GetBytes("{")));

        Assert.Equal(RequestEnvelopeFailureKind.Permanent, RequestEnvelopeFailure.GetKind(error));
    }

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
    ///     Verifies that one request type cannot carry conflicting transport descriptors. Which route
    ///     and discriminator a request uses must not depend on registration order, so the collision is
    ///     reported against the type that caused it rather than left to a dictionary's bare key error.
    /// </summary>
    [Fact]
    public void ShouldRejectConflictingTransportDescriptorsForOneRequestType()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new JsonRequestSerializer([
            Registration(typeof(UniversalAction), "test.shared.first"),
            Registration(typeof(UniversalAction), "test.shared.second")
        ], TestJson.Options()));

        Assert.Contains(nameof(UniversalAction), error.Message, StringComparison.Ordinal);
        Assert.Contains("conflicting transport descriptors", error.Message, StringComparison.Ordinal);
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
        Assert.Equal(RequestEnvelopeFailureKind.Retryable, RequestEnvelopeFailure.GetKind(error));
    }

    /// <summary>An unsupported envelope is classified without interpreting another version's body.</summary>
    [Theory]
    [InlineData("{\"version\":3}")]
    [InlineData("{\"version\":3,\"kind\":\"renamed-v3-contract\",\"body\":[]}")]
    public void ShouldRejectUnsupportedEnvelopeVersionBeforeReadingVersionSpecificFields(string json)
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));

        var error = Assert.Throws<InvalidOperationException>(() =>
            serializer.DeserializeEnvelope(Encoding.UTF8.GetBytes(json)));

        Assert.Contains("version 2 only", error.Message, StringComparison.Ordinal);
        Assert.Equal(RequestEnvelopeFailureKind.Retryable, RequestEnvelopeFailure.GetKind(error));
    }

    /// <summary>Only a readable integer can select an envelope format.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":null}")]
    [InlineData("{\"version\":\"2\"}")]
    [InlineData("{\"version\":{}}")]
    [InlineData("{\"version\":2.5}")]
    public void ShouldClassifyUnreadableEnvelopeVersionAsPermanent(string json)
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));

        var error = Assert.ThrowsAny<Exception>(() =>
            serializer.DeserializeEnvelope(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(RequestEnvelopeFailureKind.Permanent, RequestEnvelopeFailure.GetKind(error));
    }

    /// <summary>E3: Every malformed corpus member produces a classified envelope failure.</summary>
    [Theory]
    [MemberData(nameof(MalformedEnvelopeCorpus))]
    public void ShouldClassifyEveryEnvelopeFailureGivenGeneratedCorpus(string cellId, byte[] input)
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));

        var error = Record.Exception(() => serializer.DeserializeEnvelope(input));

        Assert.NotNull(error);
        Assert.True(error is System.Text.Json.JsonException || RequestEnvelopeFailure.GetKind(error) is not null,
            $"{cellId} produced unclassified {error.GetType().FullName}: {error.Message}");
    }

    /// <summary>E4: Unknown top-level members do not prevent a valid envelope from being read.</summary>
    [Fact]
    public void ShouldDeserializeValidEnvelopeGivenUnknownTopLevelProperties()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var envelope = Envelope(serializer);
        envelope["future"] = new JsonObject { ["nested"] = 7 };

        var result = serializer.DeserializeEnvelope(Bytes(envelope));

        Assert.Equal(new UniversalAction(7), result.Request);
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
        [RequestTransportId.Callable], new RequestRouteAttribute("test", "shared", "collision", "run"),
        new DiscriminatorAttribute(name));

    static JsonObject Envelope(JsonRequestSerializer serializer) =>
        JsonNode.Parse(serializer.Serialize(new UniversalAction(7), null, RequestMetadata.Create(), null).Span)!
            .AsObject();

    static ReadOnlyMemory<byte> Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    /// <summary>Builds the deterministic E3 malformed-input corpus.</summary>
    public static IEnumerable<object[]> MalformedEnvelopeCorpus()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var valid = Envelope(serializer);
        var index = 0;
        foreach (var property in new[] { "version", "contract", "contract_version", "metadata", "payload" })
        {
            var missing = (JsonObject)valid.DeepClone();
            _ = missing.Remove(property);
            yield return [$"E3-delete-{property}", Bytes(missing).ToArray()];
            foreach (var replacement in new JsonNode?[] { null, "wrong", new JsonArray(), new JsonObject() })
            {
                if (property == "payload" && replacement is JsonObject)
                    continue;
                var retyped = (JsonObject)valid.DeepClone();
                retyped[property] = replacement?.DeepClone();
                yield return [$"E3-retype-{property}-{index++}", Bytes(retyped).ToArray()];
            }
        }

        var validBytes = Bytes(valid).ToArray();
        for (var length = 0; length < validBytes.Length; length += Math.Max(1, validBytes.Length / 12))
            yield return [$"E3-truncate-{length}", validBytes[..length]];

        var random = new Random(0x504f5254);
        for (var sample = 0; sample < 12; sample++)
        {
            var bytes = new byte[random.Next(1, 48)];
            random.NextBytes(bytes);
            yield return [$"E3-random-{sample}", bytes];
        }
    }
}
