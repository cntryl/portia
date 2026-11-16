using System.Text.Json;

namespace Cntryl.Portia.Consumer;

public sealed class OpenApiJsonMemberMetadataTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldDocumentResolvedJsonMemberNameGivenApplicationMetadataRename(bool acceptsForm)
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(int Value) : IRequest<string>, ICallable;",
            "request.Value.ToString(CultureInfo.InvariantCulture)",
            $$"""
            app.MapPortiaPost<Binding, string>("/binding"){{(acceptsForm ? ".DisableAntiforgery()" : "")}};
            app.MapPortiaOpenApi();
            """,
            """
            builder.Services.AddPortia().ConfigureJson(options => options.TypeInfoResolver =
                new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
                {
                    Modifiers = { info => { if (info.Type == typeof(Binding)) info.Properties[0].Name = "custom"; } }
                });
            """);

        // Act
        var (runtimeStatus, runtimeBody) = await HttpConsumerScenario.RunAsync(assembly, "/binding", "{\"custom\":16}");

        // Assert
        Assert.Equal(200, runtimeStatus);
        Assert.Equal("\"16\"", runtimeBody);

        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/openapi/v1.json");

        Assert.Equal(200, status);
        using var document = JsonDocument.Parse(body);
        var schema = document.RootElement.GetProperty("paths").GetProperty("/binding").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.True(schema.GetProperty("properties").TryGetProperty("custom", out _));
        Assert.DoesNotContain("value", schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        if (acceptsForm)
        {
            var formSchema = document.RootElement.GetProperty("paths").GetProperty("/binding").GetProperty("post")
                .GetProperty("requestBody").GetProperty("content").GetProperty("application/x-www-form-urlencoded")
                .GetProperty("schema");
            Assert.True(formSchema.GetProperty("properties").TryGetProperty("value", out _));
            Assert.False(formSchema.GetProperty("properties").TryGetProperty("custom", out _));
        }
    }

    [Fact]
    public async Task ShouldKeepDescriptionsLocalGivenDifferentJsonAndFormWireNames()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(int Value) : IRequest<string>, ICallable;",
            "request.Value.ToString(CultureInfo.InvariantCulture)",
            """
            app.MapPortiaPost<Binding, string>("/binding").DisableAntiforgery();
            app.MapPortiaOpenApi();
            """,
            """
            builder.Services.AddPortia().ConfigureJson(options => options.TypeInfoResolver =
                new DefaultJsonTypeInfoResolver
                {
                    Modifiers = { info => { if (info.Type == typeof(Binding)) info.Properties[0].Name = "custom"; } }
                }).AddHttp(options =>
                {
                    options.OpenApi.DescribeMember("custom", "JSON value.");
                    options.OpenApi.DescribeMember("value", "Form value.");
                });
            """);

        // Act
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/openapi/v1.json");

        // Assert
        Assert.Equal(200, status);
        using var document = JsonDocument.Parse(body);
        var content = document.RootElement.GetProperty("paths").GetProperty("/binding").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content");
        Assert.Equal("JSON value.", content.GetProperty("application/json").GetProperty("schema")
            .GetProperty("properties").GetProperty("custom").GetProperty("description").GetString());
        Assert.Equal("Form value.", content.GetProperty("application/x-www-form-urlencoded").GetProperty("schema")
            .GetProperty("properties").GetProperty("value").GetProperty("description").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldResolveDescriptionIdentityGivenApplicationMetadataRename(bool parameterless)
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            parameterless
                ? "public sealed record Binding : IRequest<string>, ICallable { public int Value { get; init; } }"
                : "public sealed record Binding(int Value) : IRequest<string>, ICallable;",
            "request.Value.ToString(CultureInfo.InvariantCulture)",
            """
            app.MapPortiaPost<Binding, string>("/binding", c => c
                .Accepts<Binding>("application/json").OnBind(_ => new Binding {{VALUE}})).WithSummary("Bind input.");
            app.MapPortiaOpenApi();
            """.Replace("{{VALUE}}", parameterless ? "{ Value = 1 }" : "(1)"),
            """
            builder.Services.AddPortia().ConfigureJson(options => options.TypeInfoResolver =
                new DefaultJsonTypeInfoResolver
                {
                    Modifiers = { info => { if (info.Type == typeof(Binding)) info.Properties[0].Name = "custom"; } }
                }).AddHttp(options =>
                {
                    options.OpenApi.RequireDescriptions = true;
                    options.OpenApi.DescribeSchema<Binding>("Input.", d => d.Property(x => x.Value, "Declared value."));
                });
            """);

        // Act
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/openapi/v1.json");

        // Assert
        Assert.Equal(200, status);
        using var document = JsonDocument.Parse(body);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var schema = Assert.Single(schemas.EnumerateObject(), item => item.Name.StartsWith("Portia_", StringComparison.Ordinal)).Value;
        Assert.Equal("Input.", schema.GetProperty("description").GetString());
        Assert.Equal("Declared value.", schema.GetProperty("properties").GetProperty("custom").GetProperty("description").GetString());
    }

    [Fact]
    public async Task ShouldRejectPropertyDescriptionGivenIgnoredJsonMember()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding([property: JsonIgnore] int Value) : IRequest<string>, ICallable;",
            "request.Value.ToString(CultureInfo.InvariantCulture)",
            """
            app.MapPortiaPost<Binding, string>("/binding", c => c
                .Accepts<Binding>("application/json").OnBind(_ => new Binding(1)));
            app.MapPortiaOpenApi();
            """,
            """
            builder.Services.AddPortia().AddHttp(options => options.OpenApi
                .DescribeSchema<Binding>("Input.", d => d.Property(x => x.Value, "Ignored value.")));
            """);

        // Act
        var exception = await Record.ExceptionAsync(() => HttpConsumerScenario.RunAsync(assembly, "/openapi/v1.json"));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("not present in its JSON contract", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldDocumentMemberNumberHandlingGivenDifferentScalarContracts()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            """
            public sealed record Binding(
                [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)] int First,
                int Second) : IRequest<string>, ICallable;
            """,
            "request.First.ToString(CultureInfo.InvariantCulture) + \",\" + request.Second.ToString(CultureInfo.InvariantCulture)",
            """
            app.MapPortiaPost<Binding, string>("/binding");
            app.MapPortiaOpenApi();
            """,
            "builder.Services.AddPortia().ConfigureJson(options => options.NumberHandling = JsonNumberHandling.Strict);");

        // Act
        var (runtimeStatus, runtimeBody) = await HttpConsumerScenario.RunAsync(assembly, "/binding",
            "{\"first\":\"16\",\"second\":32}");

        // Assert
        Assert.Equal(200, runtimeStatus);
        Assert.Equal("\"16,32\"", runtimeBody);

        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/openapi/v1.json");

        Assert.Equal(200, status);
        using var document = JsonDocument.Parse(body);
        var properties = document.RootElement.GetProperty("paths").GetProperty("/binding").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema")
            .GetProperty("properties");
        Assert.Contains("\"string\"", properties.GetProperty("first").GetProperty("type").GetRawText(), StringComparison.Ordinal);
        Assert.Equal("integer", properties.GetProperty("second").GetProperty("type").GetString());
    }
}
