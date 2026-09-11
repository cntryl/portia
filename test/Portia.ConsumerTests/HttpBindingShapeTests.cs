using System.Globalization;

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
                       using Cntryl.Portia.Testing;
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
        Assert.Contains(reason, diagnostic.GetMessage(CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldReportDiagnosticGivenOptionalRouteTokenWhenMappingIsGenerated()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           using Microsoft.AspNetCore.Routing;
                                                           public sealed record Binding(int? Id) : IRequest<string>, ICallable;
                                                           public static class Scenario
                                                           {
                                                               public static void Map(IEndpointRouteBuilder app) => app.MapPortiaGet<Binding, string>("/binding/{id?}");
                                                           }
                                                           """, new RequestHttpBindingGenerator());
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA026");
        Assert.Contains("query parameter or separate endpoint", diagnostic.GetMessage(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldReportDiagnosticGivenCollidingRequestTypeNamesWhenMappingsAreGenerated()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           using Microsoft.AspNetCore.Routing;
                                                           namespace One { public sealed record CreateOrder : IRequest, ICallable; }
                                                           namespace Two { public sealed record CreateOrder : IRequest, ICallable; }
                                                           public static class Scenario
                                                           {
                                                               public static void Map(IEndpointRouteBuilder app)
                                                               {
                                                                   app.MapPortiaPost<One.CreateOrder>("/one");
                                                                   app.MapPortiaPost<Two.CreateOrder>("/two");
                                                               }
                                                           }
                                                           """, new RequestHttpBindingGenerator());
        var diagnosticsForCollision = diagnostics.Where(item => item.Id == "PORTIA027").ToArray();
        Assert.Equal(2, diagnosticsForCollision.Length);
        Assert.All(diagnosticsForCollision, diagnostic => Assert.Contains("createOrder",
            diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    [Fact]
    public void ShouldReportDiagnosticGivenRepeatedRequestTypeWhenMappingsShareOneScope()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           using Microsoft.AspNetCore.Routing;
                                                           public sealed record RefreshOrder : IRequest, ICallable;
                                                           public static class Scenario
                                                           {
                                                               public static void Map(IEndpointRouteBuilder app)
                                                               {
                                                                   app.MapPortiaGet<RefreshOrder>("/orders/refresh");
                                                                   app.MapPortiaPost<RefreshOrder>("/orders/refresh");
                                                               }
                                                           }
                                                           """, new RequestHttpBindingGenerator());

        var collisions = diagnostics.Where(item => item.Id == "PORTIA027").ToArray();
        Assert.Equal(2, collisions.Length);
        Assert.All(collisions, diagnostic => Assert.Contains("refreshOrder",
            diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(".ExcludeFromDescription()")]
    [InlineData(".ExcludeFromDescription().WithTags(\"internal\")")]
    [InlineData(".WithTags(\"internal\").ExcludeFromDescription()")]
    [InlineData(".WithTags(\"internal\").WithSummary(\"s\").ExcludeFromDescription()")]
    public void ShouldNotReportCollisionGivenOneMappingIsExcludedFromDescription(string conventions)
    {
        // Whether an endpoint is described is a property of the endpoint, not of the order its
        // conventions happen to be written in. Detecting the exclusion only when it sits directly
        // on the mapping made a hard compile error depend on formatting.
        var diagnostics = GeneratorCompilation.Diagnostics($$"""
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           using Microsoft.AspNetCore.Builder;
                                                           using Microsoft.AspNetCore.Routing;
                                                           public sealed record RefreshOrder : IRequest, ICallable;
                                                           public static class Scenario
                                                           {
                                                               public static void Map(IEndpointRouteBuilder app)
                                                               {
                                                                   app.MapPortiaGet<RefreshOrder>("/orders/refresh");
                                                                   app.MapPortiaPost<RefreshOrder>("/orders/refresh"){{conventions}};
                                                               }
                                                           }
                                                           """, new RequestHttpBindingGenerator());

        Assert.DoesNotContain(diagnostics, item => item.Id == "PORTIA027");
    }

    [Fact]
    public void UnrelatedMappingMethodIsNeverIntercepted()
    {
        var assembly = GeneratorCompilation.Compile("""
                                                    using Cntryl.Portia;
                                                    using Cntryl.Portia.Testing;
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
