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
    public async Task ShouldUseBoundRequestMemberGivenCollectionRoute()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            """
            public sealed record Binding(string AccountId) : IRequest<PortiaCreateOutcome<string>>, ICallable;
            """,
            "PortiaCreateOutcome<string>.Created(request.AccountId)",
            """
            app.MapGroup("/tenants/{tenant_id}")
                .MapPortiaPost<Binding, PortiaCreateOutcome<string>>("/accounts",
                    endpoint => endpoint.Created("/tenants/{tenant_id}/accounts/{account_id}"));
            """,
            resultType: "PortiaCreateOutcome<string>");

        // Act
        var (status, _, location) = await HttpConsumerScenario.RunWithLocationAsync(assembly,
            "/tenants/alpha%20beta/accounts", """{"accountId":"a/b#c"}""");

        // Assert
        Assert.Equal(201, status);
        Assert.Equal("/tenants/alpha%20beta/accounts/a%2Fb%23c", location);
    }

    [Fact]
    public async Task ShouldResolveLocationFromPayloadGivenServerGeneratedIdentifier()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(string Name) : IRequest<PortiaCreateOutcome<Payload>>, ICallable; public sealed record Payload(string AccountId, string Name);",
            "PortiaCreateOutcome<Payload>.Created(new Payload(\"server/id#1\", request.Name))",
            "app.MapPortiaPost<Binding, PortiaCreateOutcome<Payload>>(\"/accounts\", endpoint => endpoint.Created(\"/accounts/{account_id}\"));",
            resultType: "PortiaCreateOutcome<Payload>");

        // Act
        var (status, body, location) = await HttpConsumerScenario.RunWithLocationAsync(assembly, "/accounts",
            "{\"name\":\"Portia\"}");

        // Assert
        Assert.Equal(201, status);
        Assert.Equal("/accounts/server%2Fid%231", location);
        Assert.Equal("{\"accountId\":\"server/id#1\",\"name\":\"Portia\"}", body);
    }

    [Fact]
    public async Task ShouldPreferRequestMemberOverPayloadMemberGivenCollectionRoute()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(string AccountId) : IRequest<PortiaCreateOutcome<Payload>>, ICallable; public sealed record Payload(string AccountId);",
            "PortiaCreateOutcome<Payload>.Created(new Payload(\"payload-id\"))",
            "app.MapPortiaPost<Binding, PortiaCreateOutcome<Payload>>(\"/accounts\", endpoint => endpoint.Created(\"/accounts/{account_id}\"));",
            resultType: "PortiaCreateOutcome<Payload>");

        // Act
        var (status, _, location) = await HttpConsumerScenario.RunWithLocationAsync(assembly, "/accounts",
            "{\"accountId\":\"request-id\"}");

        // Assert
        Assert.Equal(201, status);
        Assert.Equal("/accounts/request-id", location);
    }

    [Fact]
    public async Task ShouldPreferRouteValueOverRequestAndPayloadMembersGivenCreatedLocation()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(string AccountId) : IRequest<PortiaCreateOutcome<Payload>>, ICallable; public sealed record Payload(string AccountId);",
            "PortiaCreateOutcome<Payload>.Created(new Payload(\"payload-id\"))",
            "app.MapPortiaPost<Binding, PortiaCreateOutcome<Payload>>(\"/accounts/{account_id}\", endpoint => endpoint.Created(\"/accounts/{account_id}\").OnBind(_ => new Binding(\"request-id\")).Parameter<string>(\"account_id\", PortiaHttpParameterLocation.Route));",
            resultType: "PortiaCreateOutcome<Payload>");

        // Act
        var (status, _, location) = await HttpConsumerScenario.RunWithLocationAsync(assembly, "/accounts/route-id", "{}");

        // Assert
        Assert.Equal(201, status);
        Assert.Equal("/accounts/route-id", location);
    }

    [Fact]
    public async Task ShouldRejectMissingRequestMemberBeforeDispatchGivenPayloadFallback()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(string? AccountId, string Value) : IRequest<PortiaCreateOutcome<Payload>>, ICallable; public sealed record Payload(string AccountId); public static class Probe { public static bool HandlerRan { get; private set; } public static Payload CreatePayload() { HandlerRan = true; return new Payload(\"payload-id\"); } }",
            "PortiaCreateOutcome<Payload>.Created(Probe.CreatePayload())",
            "app.MapPortiaPost<Binding, PortiaCreateOutcome<Payload>>(\"/accounts\", endpoint => endpoint.Created(\"/accounts/{account_id}\"));",
            resultType: "PortiaCreateOutcome<Payload>");

        // Act
        var (status, body, location) = await HttpConsumerScenario.RunWithLocationAsync(assembly, "/accounts",
            "{\"value\":\"same\"}");

        // Assert
        Assert.Equal(400, status);
        Assert.Contains("account_id", body, StringComparison.OrdinalIgnoreCase);
        Assert.Null(location);
        Assert.False((bool)assembly.GetType("Probe")!.GetProperty("HandlerRan")!.GetValue(null)!);
    }

    [Fact]
    public async Task ShouldFailAfterDispatchGivenMissingPayloadLocationValue()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(string Name) : IRequest<PortiaCreateOutcome<Payload>>, ICallable; public sealed record Payload(string? AccountId); public static class Probe { public static bool HandlerRan { get; private set; } public static Payload CreatePayload() { HandlerRan = true; return new Payload(null); } }",
            "PortiaCreateOutcome<Payload>.Created(Probe.CreatePayload())",
            "app.MapPortiaPost<Binding, PortiaCreateOutcome<Payload>>(\"/accounts\", endpoint => endpoint.Created(\"/accounts/{account_id}\"));",
            resultType: "PortiaCreateOutcome<Payload>");

        // Act
        var (status, _, location) = await HttpConsumerScenario.RunWithLocationAsync(assembly,
            "/accounts", "{\"name\":\"Portia\"}");

        // Assert
        Assert.Equal(500, status);
        Assert.Null(location);
        Assert.True((bool)assembly.GetType("Probe")!.GetProperty("HandlerRan")!.GetValue(null)!);
    }

    [Theory]
    [InlineData(false, 201)]
    [InlineData(true, 200)]
    public async Task ShouldReturnPayloadlessCreatedOutcomeWithLocationGivenCreateOrRepeat(bool repeated, int expectedStatus)
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(string AccountId, bool Repeated) : IRequest<PortiaCreateOutcome>, ICallable;",
            "request.Repeated ? PortiaCreateOutcome.Repeated() : PortiaCreateOutcome.Created()",
            "app.MapPortiaPost<Binding, PortiaCreateOutcome>(\"/accounts\", endpoint => endpoint.Created(\"/accounts/{account_id}\"));",
            resultType: "PortiaCreateOutcome");

        // Act
        var (status, body, location) = await HttpConsumerScenario.RunWithLocationAsync(assembly, "/accounts",
            "{\"accountId\":\"client-id\",\"repeated\":" + (repeated ? "true" : "false") + "}");

        // Assert
        Assert.Equal(expectedStatus, status);
        Assert.Equal("/accounts/client-id", location);
        Assert.Empty(body);
    }

    [Fact]
    public async Task ShouldDocumentPayloadlessCreatedResponsesWithoutContentGivenCreatedEndpoint()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(string AccountId) : IRequest<PortiaCreateOutcome>, ICallable;",
            "PortiaCreateOutcome.Created()",
            "app.MapPortiaPost<Binding, PortiaCreateOutcome>(\"/accounts\", endpoint => endpoint.Created(\"/accounts/{account_id}\")); app.MapPortiaOpenApi();",
            resultType: "PortiaCreateOutcome");

        // Act
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/openapi/v1.json");

        // Assert
        Assert.Equal(200, status);
        using var document = JsonDocument.Parse(body);
        var responses = document.RootElement.GetProperty("paths").GetProperty("/accounts").GetProperty("post")
            .GetProperty("responses");
        foreach (var code in new[] { "200", "201" })
        {
            var response = responses.GetProperty(code);
            Assert.False(response.TryGetProperty("content", out _));
            Assert.Equal("uri-reference", response.GetProperty("headers").GetProperty("Location")
                .GetProperty("schema").GetProperty("format").GetString());
        }
    }

    [Fact]
    public async Task ShouldRejectBeforeDispatchGivenOmittedCatchAllValue()
    {
        // Arrange
        var assembly = CreateScenario(
            """
            app.MapPortiaPost<Binding, PortiaCreateOutcome<Payload>>("/folders/{**parent}",
                endpoint => endpoint.Created("/folders/{parent}"));
            """,
            """
            request.Value == "same" ? throw new InvalidOperationException("The handler must not run.") :
                PortiaCreateOutcome<Payload>.Created(new Payload(request.Value))
            """);

        // Act
        var (status, body, location) = await HttpConsumerScenario.RunWithLocationAsync(assembly,
            "/folders", """{"id":"folder","value":"same"}""");

        // Assert
        Assert.Equal(400, status);
        Assert.Contains("parent", body, StringComparison.Ordinal);
        Assert.Null(location);
    }

    [Fact]
    public async Task ShouldFailGivenNullCreateOutcome()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(string Value) : IRequest<PortiaCreateOutcome<string>?>, ICallable;",
            "null",
            """
            app.MapPortiaPost<Binding, PortiaCreateOutcome<string>?>("/accounts",
                endpoint => endpoint.Created("/accounts/1"));
            """,
            resultType: "PortiaCreateOutcome<string>?");

        // Act
        var (status, _, location) = await HttpConsumerScenario.RunWithLocationAsync(assembly,
            "/accounts", """{"value":"same"}""");

        // Assert
        Assert.Equal(500, status);
        Assert.Null(location);
    }

    [Fact]
    public async Task ShouldDocumentNullablePayloadGivenCreateOutcomeOfNullableString()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(string Value) : IRequest<PortiaCreateOutcome<string?>>, ICallable;",
            "PortiaCreateOutcome<string?>.Created(null)",
            """
            app.MapPortiaPost<Binding, PortiaCreateOutcome<string?>>("/accounts",
                endpoint => endpoint.Created("/accounts/1"));
            app.MapPortiaOpenApi();
            """,
            resultType: "PortiaCreateOutcome<string?>");

        // Act
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/openapi/v1.json");

        // Assert
        Assert.Equal(200, status);
        using var document = JsonDocument.Parse(body);
        var responses = document.RootElement.GetProperty("paths").GetProperty("/accounts").GetProperty("post")
            .GetProperty("responses");
        foreach (var code in new[] { "200", "201" })
        {
            var type = responses.GetProperty(code).GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("type");
            Assert.Equal(JsonValueKind.Array, type.ValueKind);
            var types = type.EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.Contains("string", types);
            Assert.Contains("null", types);
        }
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
