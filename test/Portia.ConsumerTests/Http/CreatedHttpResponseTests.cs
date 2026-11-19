using System.Text.Json;

namespace Cntryl.Portia.Consumer;

public sealed class CreatedHttpResponseTests
{
    [Theory]
    [InlineData("first", 201)]
    [InlineData("repeat", 200)]
    public async Task ShouldReturnTypedCreateStatusAndLocationGivenHandlerOutcome(string value, int expectedStatus)
    {
        // Arrange
        var assembly = CreateScenario(
            "app.MapPortiaPost<Binding, PortiaCreateOutcome<Payload>>(\"/accounts/{id}\", endpoint => endpoint.Created(\"/accounts/{id}\"));",
            "request.Value == \"first\" ? PortiaCreateOutcome<Payload>.Created(new Payload(\"same\")) : PortiaCreateOutcome<Payload>.Repeated(new Payload(\"same\"))");

        // Act
        var (status, body, location) = await HttpConsumerScenario.RunWithLocationAsync(assembly, "/accounts/a%20b",
            "{\"value\":\"" + value + "\"}");

        // Assert
        Assert.Equal(expectedStatus, status);
        Assert.Equal("/accounts/a%20b", location);
        Assert.Equal("{\"name\":\"same\"}", body);
    }

    [Fact]
    public async Task ShouldResolveGroupedRouteValuesGivenCreatedLocationTemplate()
    {
        // Arrange
        var assembly = CreateScenario(
            "app.MapGroup(\"/api\").MapPortiaPost<Binding, PortiaCreateOutcome<Payload>>(\"/accounts/{id}\", endpoint => endpoint.Created(\"/api/accounts/{id}\"));",
            "PortiaCreateOutcome<Payload>.Created(new Payload(request.Value))");

        // Act
        var (status, body, location) = await HttpConsumerScenario.RunWithLocationAsync(assembly,
            "/api/accounts/alpha%20beta", "{\"value\":\"same\"}");

        // Assert
        Assert.Equal(201, status);
        Assert.Equal("/api/accounts/alpha%20beta", location);
        Assert.Equal("{\"name\":\"same\"}", body);
    }

    [Fact]
    public async Task ShouldRejectUnknownLocationPlaceholderGivenCreatedEndpoint()
    {
        // Arrange
        var assembly = CreateScenario(
            "app.MapPortiaPost<Binding, PortiaCreateOutcome<Payload>>(\"/accounts/{id}\", endpoint => endpoint.Created(\"/accounts/{missing}\"));",
            "PortiaCreateOutcome<Payload>.Created(new Payload(request.Value))");

        // Act
        var exception = await Record.ExceptionAsync(() => HttpConsumerScenario.RunAsync(assembly,
            "/accounts/1", "{\"value\":\"same\"}"));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("missing", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShouldRejectMalformedLocationPlaceholderGivenCreatedEndpoint()
    {
        // Arrange
        var assembly = CreateScenario(
            "app.MapPortiaPost<Binding, PortiaCreateOutcome<Payload>>(\"/accounts/{id}\", endpoint => endpoint.Created(\"/accounts/{id\"));",
            "PortiaCreateOutcome<Payload>.Created(new Payload(request.Value))");

        // Act
        var exception = await Record.ExceptionAsync(() => HttpConsumerScenario.RunAsync(assembly,
            "/accounts/1", "{\"value\":\"same\"}"));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("malformed placeholders", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShouldDocumentBothCreateStatusesAndUnwrappedSchemaGivenCreatedEndpoint()
    {
        // Arrange
        var assembly = CreateScenario(
            "app.MapPortiaPost<Binding, PortiaCreateOutcome<Payload>>(\"/accounts/{id}\", endpoint => endpoint.Created(\"/accounts/{id}\").DescribeResponse(201, \"New account.\").DescribeResponse(200, \"Previously created account.\").ResponseExample(201, \"created\", new Payload(\"example\")).ResponseExample(200, \"repeat\", new Payload(\"example\"))); app.MapPortiaOpenApi();",
            "PortiaCreateOutcome<Payload>.Created(new Payload(request.Value))");

        // Act
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/openapi/v1.json");

        // Assert
        Assert.Equal(200, status);
        using var document = JsonDocument.Parse(body);
        var operation = document.RootElement.GetProperty("paths").GetProperty("/accounts/{id}").GetProperty("post");
        var responses = operation.GetProperty("responses");
        var response201 = responses.GetProperty("201");
        var response200 = responses.GetProperty("200");
        Assert.Equal(response201.GetProperty("content").GetProperty("application/json").GetProperty("schema").GetRawText(),
            response200.GetProperty("content").GetProperty("application/json").GetProperty("schema").GetRawText());
        Assert.Equal("New account.", response201.GetProperty("description").GetString());
        Assert.Equal("Previously created account.", response200.GetProperty("description").GetString());
        var payloadSchema = response201.GetProperty("content").GetProperty("application/json").GetProperty("schema");
        if (payloadSchema.TryGetProperty("$ref", out var schemaRef))
        {
            var schemaName = schemaRef.GetString()!.Split('/').Last();
            payloadSchema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(schemaName);
        }
        Assert.Equal("object", payloadSchema.GetProperty("type").GetString());
        Assert.True(payloadSchema.GetProperty("properties").TryGetProperty("name", out _));
        Assert.True(response201.GetProperty("content").GetProperty("application/json").GetProperty("examples")
            .TryGetProperty("created", out _));
        Assert.True(response200.GetProperty("content").GetProperty("application/json").GetProperty("examples")
            .TryGetProperty("repeat", out _));
        foreach (var response in new[] { response201, response200 })
        {
            Assert.Equal("string", response.GetProperty("headers").GetProperty("Location").GetProperty("schema")
                .GetProperty("type").GetString());
            Assert.Equal("uri-reference", response.GetProperty("headers").GetProperty("Location").GetProperty("schema")
                .GetProperty("format").GetString());
        }
        Assert.DoesNotContain("PortiaCreateOutcome", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldKeepOrdinarySuccessResponseGivenUnconfiguredEndpoint()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(int Id) : IRequest<string>, ICallable;",
            "request.Id.ToString(CultureInfo.InvariantCulture)",
            "app.MapPortiaGet<Binding, string>(\"/accounts/{id}\"); app.MapPortiaOpenApi();");

        // Act
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/openapi/v1.json");

        // Assert
        Assert.Equal(200, status);
        using var document = JsonDocument.Parse(body);
        var responses = document.RootElement.GetProperty("paths").GetProperty("/accounts/{id}").GetProperty("get")
            .GetProperty("responses");
        Assert.True(responses.TryGetProperty("200", out _));
        Assert.False(responses.TryGetProperty("201", out _));
    }

    [Fact]
    public async Task ShouldRejectCreatedGivenOrdinaryResultType()
    {
        // Arrange
        const string declaration = "public sealed record Binding(string Value) : IRequest<string>, ICallable;";
        var assembly = HttpConsumerScenario.Compile(declaration, "request.Value",
            "app.MapPortiaPost<Binding, string>(\"/accounts\", endpoint => endpoint.Created(\"/accounts/{id}\"));");

        // Act
        var exception = await Record.ExceptionAsync(() => HttpConsumerScenario.RunAsync(assembly, "/accounts",
            "{\"value\":\"ordinary\"}"));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("PortiaCreateOutcome", failure.Message, StringComparison.Ordinal);
    }

    static System.Reflection.Assembly CreateScenario(string mapping, string handle) => HttpConsumerScenario.Compile(
        "public sealed record Binding(string Id, string Value) : IRequest<PortiaCreateOutcome<Payload>>, ICallable; public sealed record Payload(string Name);",
        handle, mapping, resultType: "PortiaCreateOutcome<Payload>");
}
