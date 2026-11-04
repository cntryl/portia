using System.Text.Json;

namespace Cntryl.Portia.Consumer;

public sealed class OpenApiJsonMemberMetadataTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldDocumentResolvedJsonMemberNameGivenApplicationMetadataRename(bool acceptsForm)
    {
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
        var (runtimeStatus, runtimeBody) = await HttpConsumerScenario.RunAsync(assembly, "/binding", "{\"custom\":16}");
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
    public async Task ShouldDocumentMemberNumberHandlingGivenDifferentScalarContracts()
    {
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
        var (runtimeStatus, runtimeBody) = await HttpConsumerScenario.RunAsync(assembly, "/binding",
            "{\"first\":\"16\",\"second\":32}");
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
