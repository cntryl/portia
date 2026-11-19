using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace Cntryl.Portia.ReflectionDisabled;

public sealed class ReflectionDisabledConsumerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldRoundTripCreateResultGivenSourceGeneratedRpcSerialization(bool created)
    {
        // Arrange
        var serializer = new JsonRequestSerializer([], ReflectionDisabledJsonContext.Default.Options);
        var outcome = created ? PortiaCreateOutcome<string>.Created("Portia") :
            PortiaCreateOutcome<string>.Repeated("Portia");

        // Act
        var bytes = serializer.SerializeResult(Result<PortiaCreateOutcome<string>>.Success(outcome));
        var result = serializer.DeserializeResult<PortiaCreateOutcome<string>>(bytes);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("Portia", result.Value.Value);
        Assert.Equal(created, result.Value.IsCreated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldRoundTripPayloadlessCreateResultGivenSourceGeneratedRpcSerialization(bool created)
    {
        // Arrange
        var serializer = new JsonRequestSerializer([], ReflectionDisabledJsonContext.Default.Options);
        var outcome = created ? PortiaCreateOutcome.Created() : PortiaCreateOutcome.Repeated();

        // Act
        var bytes = serializer.SerializeResult(Result<PortiaCreateOutcome>.Success(outcome));
        var result = serializer.DeserializeResult<PortiaCreateOutcome>(bytes);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(created, result.Value.IsCreated);
    }

    [Fact]
    public void ShouldRoundTripRequestEnvelopeGivenReflectionDisabled()
    {
        // Arrange
        var options = new JsonSerializerOptions(ReflectionDisabledJsonContext.Default.Options);
        var registration = new RequestTransportRegistration(typeof(CreateGreeting), [RequestTransportId.Callable],
            new RequestRouteAttribute("public", "greetings", "messages", "create"),
            new DiscriminatorAttribute("greetings.create"));
        var requests = new JsonRequestSerializer([registration], options);
        var bytes = requests.Serialize(new CreateGreeting("Portia"), null, RequestMetadata.Create(), null);

        // Act
        var envelope = requests.DeserializeEnvelope(bytes);

        // Assert
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        Assert.Equal(new CreateGreeting("Portia"), envelope.Request);
    }

    [Fact]
    public void ShouldRoundTripEventEnvelopeGivenReflectionDisabled()
    {
        // Arrange
        var options = new JsonSerializerOptions(ReflectionDisabledJsonContext.Default.Options);
        var events = new JsonDomainEventSerializer(
            new DomainEventTypeCatalog().Register<GreetingCreated>(1, "greetings.created"), null, options);
        var created = new GreetingCreated("Portia");
        created.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), Uuid.CreateVersion4(), 1,
            DateTimeOffset.UtcNow));
        var persisted = events.Serialize(created);

        // Act
        var deserialized = events.Deserialize(persisted);

        // Assert
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        _ = Assert.IsType<GreetingCreated>(deserialized);
    }

    [Fact]
    public async Task ShouldAcceptValidPostGivenGeneratedHttpBindingWithoutReflection()
    {
        // Arrange
        await using var app = await StartGreetingAppAsync();
        using var content = new StringContent(
            JsonSerializer.Serialize(new CreateGreeting("Portia"),
                ReflectionDisabledJsonContext.Default.CreateGreeting),
            Encoding.UTF8, "application/json");

        // Act
        using var response = await app.GetTestClient().PostAsync("/greetings", content);

        // Assert
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"Hello, Portia!\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ShouldRejectInvalidPostGivenGeneratedHttpBindingWithoutReflection()
    {
        // Arrange
        await using var app = await StartGreetingAppAsync();
        using var invalidContent = new StringContent(
            JsonSerializer.Serialize(new CreateGreeting(string.Empty),
                ReflectionDisabledJsonContext.Default.CreateGreeting),
            Encoding.UTF8, "application/json");

        // Act
        using var invalid = await app.GetTestClient().PostAsync("/greetings", invalidContent);

        // Assert
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("A name is required.", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldServeOpenApiDocumentGivenGeneratedHttpBindingWithoutReflection()
    {
        // Arrange
        await using var app = await StartGreetingAppAsync();

        // Act
        using var openApi = await app.GetTestClient().GetAsync("/openapi/v1.json");

        // Assert
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        var document = await openApi.Content.ReadAsStringAsync();
        Assert.Contains("\"openapi\": \"3.1.2\"", document, StringComparison.Ordinal);
        Assert.Contains("\"operationId\": \"createGreeting\"", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldReportEveryMissingRootGivenHostStartupWithUnregisteredRoots()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        var portia = builder.Services.AddPortia();
        _ = portia.AddGeneratedRequest(Registration<MissingFirst>());
        _ = portia.AddGeneratedRequest(Registration<MissingSecond>());
        _ = portia.AddWorkers();
        await using var app = builder.Build();

        // Act
        var thrown = await Record.ExceptionAsync(() => app.StartAsync());

        // Assert
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var exception = Assert.IsType<InvalidOperationException>(thrown);
        Assert.Contains(typeof(MissingFirst).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(MissingSecond).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.True(exception.Message.IndexOf(typeof(MissingFirst).FullName!, StringComparison.Ordinal)
                    < exception.Message.IndexOf(typeof(MissingSecond).FullName!, StringComparison.Ordinal));
    }

    static async Task<WebApplication> StartGreetingAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        var portia = builder.Services.AddPortia();
        _ = portia.AddHttp();
        _ = portia.AddRequestHandler<CreateGreetingHandler>();
        var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<CreateGreeting, string>("/greetings");
        await app.StartAsync();
        return app;
    }

    static RequestTransportRegistration Registration<T>() where T : IRequest => new(typeof(T),
        [RequestTransportId.Callable],
        new RequestRouteAttribute("test", "missing", "roots", typeof(T).Name),
        new DiscriminatorAttribute(typeof(T).Name));
}
