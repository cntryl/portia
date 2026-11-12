using System.Globalization;

namespace Cntryl.Portia.Consumer;

public sealed class GeneratedHttpMappingContractTests
{
    [Fact]
    public async Task QualifiedStaticHttpMappingPreservesGeneratedBinding()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(int Id) : IRequest<string>, ICallable;",
            "request.Id.ToString(CultureInfo.InvariantCulture)",
            "PortiaEndpointRouteBuilderExtensions.MapPortiaGet<Binding, string>(app, \"/binding/{id}\");");

        // Act
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/binding/7");

        // Assert
        Assert.Equal(200, status);
        Assert.Equal("\"7\"", body);
    }

    [Fact]
    public async Task PropertyConverterUsesApplicationJsonContract()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding([property: JsonConverter(typeof(HexConverter))] int Value) : IRequest<string>, ICallable;",
            "request.Value.ToString(CultureInfo.InvariantCulture)",
            "app.MapPortiaPost<Binding, string>(\"/binding\");");

        // Act
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/binding", /*lang=json,strict*/
            "{\"value\":\"0x10\"}", converter: true);

        // Assert
        Assert.Equal(200, status);
        Assert.Equal("\"16\"", body);
    }

    [Fact]
    public async Task JsonTypeInfoPropertyNamesAreHonored()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(int Value) : IRequest<string>, ICallable;",
            "request.Value.ToString(CultureInfo.InvariantCulture)",
            "app.MapPortiaPost<Binding, string>(\"/binding\");",
            """
            builder.Services.AddPortia().ConfigureJson(options => options.TypeInfoResolver =
                new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
                {
                    Modifiers = { info => { if (info.Type == typeof(Binding)) info.Properties[0].Name = "custom"; } }
                });
            """);

        // Act
        var (status, body) =
            await HttpConsumerScenario.RunAsync(assembly, "/binding", /*lang=json,strict*/ "{\"custom\":16}");

        // Assert
        Assert.Equal(200, status);
        Assert.Equal("\"16\"", body);
    }

    [Fact]
    public async Task PropertyNumberHandlingOverridesWebDefaults()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(int Value) : IRequest<string>, ICallable;",
            "request.Value.ToString(CultureInfo.InvariantCulture)",
            "app.MapPortiaPost<Binding, string>(\"/binding\");",
            "builder.Services.AddPortia().ConfigureJson(options => options.NumberHandling = JsonNumberHandling.Strict);");

        // Act
        var (status, _) =
            await HttpConsumerScenario.RunAsync(assembly, "/binding", /*lang=json,strict*/ "{\"value\":\"16\"}");

        // Assert
        Assert.Equal(400, status);
    }

    [Fact]
    public void ResultBearingQueueHttpMappingHasActionableDiagnostic()
    {
        // Arrange
        const string source = """
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           using Microsoft.AspNetCore.Routing;
                                                           public sealed record Binding(int Value) : IRequest<string>, ICallable, IQueuable;
                                                           public static class Scenario
                                                           {
                                                               public static void Map(IEndpointRouteBuilder app) => app.MapPortiaPost<Binding, string>("/binding");
                                                           }
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new RequestHttpBindingGenerator());

        // Assert
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA016");
        Assert.Contains("no-result", diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
