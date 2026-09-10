namespace Cntryl.Portia;

/// <summary>
/// Verifies <see cref="JsonRequestSerializer" /> round-trips requests, outcomes, and the actor
/// token carried alongside a request correctly — the concrete serializer every Fitz transport
/// test (queue, RPC) ultimately depends on.
/// </summary>
public sealed class JsonRequestSerializerTests
{
    /// <summary>
    /// Verifies that a request round-trips to an equal instance, alongside its actor token.
    /// </summary>
    [Fact]
    public void ShouldRoundTripRequestWithActorToken()
    {
        var serializer = TestJson.Serializer(typeof(SerializerTestRequest));
        var request = new SerializerTestRequest("hello", 42);

        var bytes = serializer.Serialize(request, "some-token", RequestMetadata.Create(), null);
        var envelope = serializer.DeserializeEnvelope(bytes);

        Assert.Equal(request, envelope.Request);
        Assert.Equal("some-token", envelope.ActorToken);
    }

    /// <summary>
    /// Verifies that a request with no actor token round-trips with a null token, not an empty
    /// string or a missing-field exception.
    /// </summary>
    [Fact]
    public void ShouldRoundTripRequestWithNullActorToken()
    {
        var serializer = TestJson.Serializer(typeof(SerializerTestRequest));
        var request = new SerializerTestRequest("hello", 42);

        var bytes = serializer.Serialize(request, null, RequestMetadata.Create(), null);
        var envelope = serializer.DeserializeEnvelope(bytes);

        Assert.Null(envelope.ActorToken);
    }

    /// <summary>
    /// Verifies that a successful no-result outcome round-trips as success, with no error.
    /// </summary>
    [Fact]
    public void ShouldRoundTripSuccessfulOutcome()
    {
        var serializer = TestJson.Serializer();

        var bytes = serializer.SerializeOutcome(Result.Success);
        var outcome = serializer.DeserializeOutcome(bytes);

        Assert.True(outcome.IsSuccess);
    }

    /// <summary>
    /// Verifies that a failed no-result outcome round-trips with its error kind, message, and
    /// transience preserved.
    /// </summary>
    [Fact]
    public void ShouldRoundTripFailedOutcomeWithErrorDetails()
    {
        var serializer = TestJson.Serializer();
        var error = new RequestError(RequestErrorKind.Conflict, "Already exists.", isTransient: true);

        var bytes = serializer.SerializeOutcome(Result.Failure(error));
        var outcome = serializer.DeserializeOutcome(bytes);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(RequestErrorKind.Conflict, outcome.Error.Kind);
        Assert.Equal("Already exists.", outcome.Error.Message);
        Assert.True(outcome.Error.IsTransient);
    }

    /// <summary>
    /// Verifies that a successful with-result outcome round-trips with its value intact.
    /// </summary>
    [Fact]
    public void ShouldRoundTripSuccessfulResultWithValue()
    {
        var serializer = TestJson.Serializer();

        var bytes = serializer.SerializeResult(Result<string>.Success("the-value"));
        var result = serializer.DeserializeResult<string>(bytes);

        Assert.True(result.IsSuccess);
        Assert.Equal("the-value", result.Value);
    }

    /// <summary>
    /// Verifies that a failed with-result outcome round-trips its error without a value.
    /// </summary>
    [Fact]
    public void ShouldRoundTripFailedResult()
    {
        var serializer = TestJson.Serializer();
        var error = new RequestError(RequestErrorKind.NotFound, "Missing.");

        var bytes = serializer.SerializeResult(Result<string>.Failure(error));
        var result = serializer.DeserializeResult<string>(bytes);

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.NotFound, result.Error.Kind);
    }

    /// <summary>
    /// Verifies that a request field of type <see cref="Uuid" /> round-trips correctly through
    /// the serializer's source-generated envelope — proving <see cref="UuidJsonConverter" />
    /// still applies at this JSON boundary, not just at the HTTP one.
    /// </summary>
    [Fact]
    public void ShouldRoundTripUuidFieldOnRequest()
    {
        var serializer = TestJson.Serializer(typeof(SerializerTestUuidRequest));
        var id = Uuid.CreateVersion4();
        var request = new SerializerTestUuidRequest(id);

        var bytes = serializer.Serialize(request, null, RequestMetadata.Create(), null);
        var envelope = serializer.DeserializeEnvelope(bytes);

        Assert.Equal(id, Assert.IsType<SerializerTestUuidRequest>(envelope.Request).Id);
    }
}

sealed record SerializerTestRequest(string Name, int Count) : IRequest;

sealed record SerializerTestUuidRequest(Uuid Id) : IRequest;
