using System.Globalization;

namespace Cntryl.Portia.Consumer;

public sealed class HttpBindingShapeTests
{
    [Fact]
    public async Task HelperConfiguredBindersSupportOtherwiseUnbindableRequests()
    {
        var assembly = GeneratorCompilation.Compile("""
                                                    using System.Net.Http;
                                                    using System.Text.Json.Serialization.Metadata;
                                                    using System.Threading;
                                                    using System.Threading.Tasks;
                                                    using Cntryl.Portia;
                                                    using Cntryl.Portia.Testing;
                                                    using Microsoft.AspNetCore.Builder;
                                                    using Microsoft.AspNetCore.Hosting;
                                                    using Microsoft.AspNetCore.TestHost;
                                                    using Microsoft.Extensions.DependencyInjection;
                                                    public sealed class MethodRequest : IRequest, ICallable
                                                    {
                                                        MethodRequest(string value) => Value = value;
                                                        public string Value { get; }
                                                        public static MethodRequest Create(string value) => new(value);
                                                    }
                                                    public sealed class LambdaRequest : IRequest, ICallable
                                                    {
                                                        LambdaRequest(string value) => Value = value;
                                                        public string Value { get; }
                                                        public static LambdaRequest Create(string value) => new(value);
                                                    }
                                                    public sealed class MethodHandler : IRequestHandler<MethodRequest>
                                                    {
                                                        public static string? Observed;
                                                        public ValueTask<Result> HandleAsync(IRequestContext<MethodRequest> context, CancellationToken ct)
                                                        {
                                                            Observed = context.Request.Value;
                                                            return ValueTask.FromResult(Result.Success);
                                                        }
                                                    }
                                                    public sealed class LambdaHandler : IRequestHandler<LambdaRequest>
                                                    {
                                                        public static string? Observed;
                                                        public ValueTask<Result> HandleAsync(IRequestContext<LambdaRequest> context, CancellationToken ct)
                                                        {
                                                            Observed = context.Request.Value;
                                                            return ValueTask.FromResult(Result.Success);
                                                        }
                                                    }
                                                    public static class Scenario
                                                    {
                                                        static void Configure(PortiaEndpointConfiguration<MethodRequest> endpoint)
                                                            => endpoint.NoInput().OnBind(_ => MethodRequest.Create("method"));
                                                        static void Configure(PortiaEndpointConfiguration<LambdaRequest> endpoint)
                                                            => endpoint.NoInput().OnBind(_ => LambdaRequest.Create("lambda"));
                                                        public static async Task<(int, int, string?, string?)> Run()
                                                        {
                                                            var builder = WebApplication.CreateBuilder();
                                                            builder.WebHost.UseTestServer();
                                                            builder.Services.AddPortia()
                                                                .ConfigureJson(options => options.TypeInfoResolver = new DefaultJsonTypeInfoResolver())
                                                                .AddRequestHandler<MethodHandler>()
                                                                .AddRequestHandler<LambdaHandler>();
                                                            await using var app = builder.Build();
                                                            app.MapPortiaPost<MethodRequest>("/method", Configure);
                                                            app.MapPortiaPost<LambdaRequest>("/lambda", endpoint => Configure(endpoint));
                                                            await app.StartAsync();
                                                            using var client = app.GetTestClient();
                                                            using var method = await client.PostAsync("/method", null);
                                                            using var lambda = await client.PostAsync("/lambda", null);
                                                            return ((int)method.StatusCode, (int)lambda.StatusCode, MethodHandler.Observed, LambdaHandler.Observed);
                                                        }
                                                    }
                                                    """, new RegistrationCallInterceptorGenerator(),
            new RequestHttpBindingGenerator());

        var result = await (Task<(int, int, string?, string?)>)assembly.GetType("Scenario")!.GetMethod("Run")!
            .Invoke(null, null)!;
        Assert.Equal((204, 204, "method", "lambda"), result);
    }

    [Fact]
    public void ConfiguredUnbindableRequestWithoutOnBindFailsDuringMapping()
    {
        var assembly = GeneratorCompilation.Compile("""
                                                    using Cntryl.Portia;
                                                    using Cntryl.Portia.Testing;
                                                    using Microsoft.AspNetCore.Builder;
                                                    public sealed class Unbindable : IRequest, ICallable
                                                    {
                                                        private Unbindable() { }
                                                    }
                                                    public static class Scenario
                                                    {
                                                        public static string Run()
                                                        {
                                                            var app = WebApplication.CreateBuilder().Build();
                                                            try
                                                            {
                                                                app.MapPortiaPost<Unbindable>("/missing", endpoint => endpoint.NoInput());
                                                                return "mapping unexpectedly succeeded";
                                                            }
                                                            catch (System.InvalidOperationException error)
                                                            {
                                                                return error.Message;
                                                            }
                                                        }
                                                    }
                                                    """, new RequestHttpBindingGenerator());

        var message = (string)assembly.GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("This request shape requires OnBind.", message);
    }

    [Fact]
    public async Task SupportedRequestRetainsDefaultBinderWhenConditionalOnBindIsNotInstalled()
    {
        var assembly = GeneratorCompilation.Compile("""
                                                    using System.Net.Http;
                                                    using System.Text.Json.Serialization.Metadata;
                                                    using System.Threading;
                                                    using System.Threading.Tasks;
                                                    using Cntryl.Portia;
                                                    using Cntryl.Portia.Testing;
                                                    using Microsoft.AspNetCore.Builder;
                                                    using Microsoft.AspNetCore.Hosting;
                                                    using Microsoft.AspNetCore.TestHost;
                                                    using Microsoft.Extensions.DependencyInjection;
                                                    public sealed record Query(string Value) : IRequest, ICallable;
                                                    public sealed class Handler : IRequestHandler<Query>
                                                    {
                                                        public static string? Observed;
                                                        public ValueTask<Result> HandleAsync(IRequestContext<Query> context, CancellationToken ct)
                                                        {
                                                            Observed = context.Request.Value;
                                                            return ValueTask.FromResult(Result.Success);
                                                        }
                                                    }
                                                    public static class Scenario
                                                    {
                                                        public static async Task<(int, string?)> Run()
                                                        {
                                                            var builder = WebApplication.CreateBuilder();
                                                            builder.WebHost.UseTestServer();
                                                            builder.Services.AddPortia()
                                                                .ConfigureJson(options => options.TypeInfoResolver = new DefaultJsonTypeInfoResolver())
                                                                .AddRequestHandler<Handler>();
                                                            await using var app = builder.Build();
                                                            app.MapPortiaGet<Query>("/query", endpoint =>
                                                            {
                                                                if (System.DateTime.UtcNow.Year < 0)
                                                                    endpoint.NoInput().OnBind(_ => new Query("custom"));
                                                            });
                                                            await app.StartAsync();
                                                            using var client = app.GetTestClient();
                                                            using var response = await client.GetAsync("/query?value=default");
                                                            return ((int)response.StatusCode, Handler.Observed);
                                                        }
                                                    }
                                                    """, new RegistrationCallInterceptorGenerator(),
            new RequestHttpBindingGenerator());

        var result = await (Task<(int, string?)>)assembly.GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal((204, "default"), result);
    }

    [Fact]
    public void ConfiguredUnaryAndStreamingMappingsAreIntercepted() => _ = GeneratorCompilation.Compile("""
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           using Microsoft.AspNetCore.Http;
                                                           using Microsoft.AspNetCore.Routing;
                                                           using System.Threading.Tasks;
                                                           public sealed record Query(string Value) : IRequest<string>, ICallable;
                                                           public sealed record StreamQuery(string Value) : IStreamRequest<string>, ICallable;
                                                           public static class Scenario
                                                           {
                                                               public static void Map(IEndpointRouteBuilder app)
                                                               {
                                                                   app.MapPortiaGet<Query, string>("/query", endpoint => endpoint.NoInput()
                                                                       .OnBind(_ => new Query("custom"))
                                                                       .OnResult((_, result) => result.IsSuccess ? Results.Ok() : null));
                                                                   app.MapPortiaGetStream<StreamQuery, string>("/stream", endpoint => endpoint.NoInput()
                                                                       .OnBind((_, _) => ValueTask.FromResult(new StreamQuery("custom"))));
                                                               }
                                                           }
                                                           """, new RequestHttpBindingGenerator());

    [Fact]
    public void CustomBinderSupportsShapesRejectedByGeneratedBindingIncludingStructs() => _ = GeneratorCompilation.Compile("""
                                         using Cntryl.Portia;
                                         using Cntryl.Portia.Testing;
                                         using Microsoft.AspNetCore.Routing;
                                         public sealed class PrivateRequest : IRequest, ICallable { private PrivateRequest() { } public static PrivateRequest Create() => new(); }
                                         public readonly record struct StructRequest(int Value) : IRequest, ICallable;
                                         public static class Scenario
                                         {
                                             public static void Map(IEndpointRouteBuilder app)
                                             {
                                                 app.MapPortiaPost<PrivateRequest>("/private", endpoint => endpoint.NoInput().OnBind(_ => PrivateRequest.Create()));
                                                 app.MapPortiaPost<StructRequest>("/struct", endpoint => endpoint.NoInput().OnBind(_ => new StructRequest(1)));
                                             }
                                         }
                                         """, new RequestHttpBindingGenerator());

    [Fact]
    public void StreamingConfigurationDoesNotExposeOnResult()
    {
        Assert.DoesNotContain(typeof(PortiaStreamingEndpointConfiguration<object>).GetMethods(),
            method => method.Name == "OnResult");
    }

    [Fact]
    public Task BodyWithStyleOnlyTryParseRemainsJsonOnly() => AssertJsonOnlyTryParseShape("""
        public enum SomeStyle { Default }
        public sealed record Scalar(string Text)
        {
            public static bool TryParse(string value, SomeStyle style, out Scalar result)
            {
                result = new Scalar(value);
                return true;
            }
        }
        """);

    [Fact]
    public Task BodyWithMismatchedTryParseOutputRemainsJsonOnly() => AssertJsonOnlyTryParseShape("""
        public sealed record Scalar(string Text)
        {
            public static bool TryParse(string value, out int result) => int.TryParse(value, out result);
        }
        """);

    [Fact]
    public async Task ValidSimpleTryParseWinsOverInvalidProviderOverload()
    {
        var assembly = HttpConsumerScenario.Compile("""
            public sealed record Scalar(string Text)
            {
                public static bool TryParse(string value, out Scalar result)
                {
                    result = new Scalar(value);
                    return true;
                }
                public static bool TryParse(string value, IFormatProvider provider, out int result)
                    => int.TryParse(value, provider, out result);
            }
            public sealed record Binding(Scalar Value) : IRequest<string>, ICallable;
            """, "request.Value.Text", "app.MapPortiaPost<Binding, string>(\"/binding\");");

        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/binding", """{"value":{"text":"json"}}""");

        Assert.Equal(200, status);
        Assert.Equal("\"json\"", body);
    }

    [Fact]
    public void InvalidTryParseShapeRemainsUnsupportedForQueryBinding()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           using Microsoft.AspNetCore.Routing;
                                                           public enum SomeStyle { Default }
                                                           public sealed record Scalar(string Text)
                                                           {
                                                               public static bool TryParse(string value, SomeStyle style, out Scalar result)
                                                               {
                                                                   result = new Scalar(value);
                                                                   return true;
                                                               }
                                                           }
                                                           public sealed record Binding(Scalar Value) : IRequest<string>, ICallable;
                                                           public static class Scenario
                                                           {
                                                               public static void Map(IEndpointRouteBuilder app)
                                                                   => app.MapPortiaGet<Binding, string>("/binding");
                                                           }
                                                           """, new RequestHttpBindingGenerator());

        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA016");
        Assert.Contains("supported scalar TryParse", diagnostic.GetMessage(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

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

    static async Task AssertJsonOnlyTryParseShape(string scalarDeclaration)
    {
        var assembly = HttpConsumerScenario.Compile($$"""
            {{scalarDeclaration}}
            public sealed record Binding(Scalar Value) : IRequest<string>, ICallable;
            """, "request.Value.Text", "app.MapPortiaPost<Binding, string>(\"/binding\");");

        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/binding", """{"value":{"text":"json"}}""");

        Assert.Equal(200, status);
        Assert.Equal("\"json\"", body);
    }
}
