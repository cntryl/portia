using System.Text.Json;

namespace Cntryl.Portia.Consumer;

public sealed class OpenApiJsonMemberMetadataTests
{
    [Fact]
    public async Task ShouldDocumentResolvedJsonMemberNameGivenApplicationMetadataRename()
    {
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(int Value) : IRequest<string>, ICallable;",
            "request.Value.ToString(CultureInfo.InvariantCulture)",
            """
            app.MapPortiaPost<Binding, string>("/binding");
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
    }
}
