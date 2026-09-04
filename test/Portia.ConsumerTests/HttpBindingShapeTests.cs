namespace Cntryl.Portia.Consumer;

public sealed class HttpBindingShapeTests
{
    [Theory]
    [InlineData("object Value", "\"/binding\"", "scalar")]
    [InlineData("int Value", "route", "constant")]
    public void UnsupportedBindingsHaveActionableDiagnostics(string parameters, string route, string reason)
    {
        var source = $$"""
            using Cntryl.Portia;
            using Microsoft.AspNetCore.Routing;
            public sealed record Binding({{parameters}}) : IRequest<string>, ICallable;
            public static class Scenario
            {
                public static void Map(IEndpointRouteBuilder app, string route)
                    => app.MapPortiaGet<Binding, string>({{route}});
            }
            """;
        var diagnostics = GeneratorCompilation.Diagnostics(source, new RequestHttpBindingGenerator());
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA016");
        Assert.Contains(reason, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/binding", "null")]
    [InlineData("/binding/7", "7")]
    public async Task OptionalRouteTokenPreservesNullableValue(string path, string expected)
    {
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(int? Id) : IRequest<string>, ICallable;",
            "request.Id?.ToString(CultureInfo.InvariantCulture) ?? \"null\"",
            "app.MapPortiaGet<Binding, string>(\"/binding/{id?}\");");
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, path);
        Assert.Equal(200, status);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected), body);
    }

    [Fact]
    public void UnrelatedMappingMethodIsNeverIntercepted()
    {
        var assembly = GeneratorCompilation.Compile("""
            using Cntryl.Portia;
            public sealed record Binding(int Value) : IRequest<string>, ICallable;
            public sealed class Unrelated
            {
                public int MapPortiaGet<TRequest, TOut>(string route) => 321;
            }
            public static class Scenario
            {
                public static int Run() => new Unrelated().MapPortiaGet<Binding, string>("/binding");
            }
            """, new RequestHttpBindingGenerator());
        Assert.Equal(321, assembly.GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null));
    }

    [Fact]
    public async Task ScalarParsingUsesInvariantCulture()
    {
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(decimal Amount) : IRequest<string>, ICallable;",
            "request.Amount.ToString(CultureInfo.InvariantCulture)",
            "CultureInfo.CurrentCulture = new CultureInfo(\"fr-FR\"); app.MapPortiaGet<Binding, string>(\"/binding\");");
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/binding?amount=12.5");
        Assert.Equal(200, status);
        Assert.Equal("\"12.5\"", body);
    }
}
